using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Sc2XInput.Native;

namespace Sc2XInput.SteamController;

internal enum ScPad : byte { Left = 0, Right = 1 }

/// <summary>
/// One Steam Controller vendor HID interface: the wired controller, or one of the four
/// slots on the wireless dongle (a slot exists whether or not a controller is paired to it).
/// </summary>
internal sealed unsafe class ScDevice : IDisposable
{
    public string Path { get; }
    public bool IsWireless { get; }
    public string Name { get; }

    /// <summary>Raised on the reader thread with the report payload (report-ID byte stripped).</summary>
    public event Action<ScDevice, ReadOnlyMemory<byte>>? ReportReceived;

    /// <summary>Raised on the reader thread when the device goes away (unplugged, read error).</summary>
    public event Action<ScDevice>? Closed;

    private readonly SafeFileHandle _readHandle;
    private readonly SafeFileHandle _ctrlHandle; // separate synchronous handle so feature reports don't block on the pending read
    private readonly int _inputLength;
    private readonly int _featureLength;
    private readonly object _ctrlLock = new();
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;
    private volatile bool _disposed;

    private ScDevice(string path, bool wireless, string name, SafeFileHandle read, SafeFileHandle ctrl, int inLen, int featLen)
    {
        Path = path;
        IsWireless = wireless;
        Name = name;
        _readHandle = read;
        _ctrlHandle = ctrl;
        _inputLength = inLen;
        _featureLength = featLen;
    }

    public static bool IsCandidatePath(string path)
    {
        var p = path.ToLowerInvariant();
        return p.Contains("vid_28de") && (p.Contains("pid_1102") || p.Contains("pid_1142"));
    }

    /// <summary>Opens the path if it is a Steam Controller vendor interface; returns null otherwise.</summary>
    public static ScDevice? TryOpen(string path)
    {
        if (!IsCandidatePath(path)) return null;

        // Query caps with zero access first: the keyboard/mouse collections are held exclusively by Windows.
        using (var probe = Win32.CreateFile(path, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (probe.IsInvalid) return null;
            if (!Win32.TryGetCaps(probe, out var caps) || caps.UsagePage != Win32.HID_USAGE_PAGE_VENDOR) return null;
            if (caps.InputReportByteLength < 65 || caps.FeatureReportByteLength < 65) return null;

            var rw = Win32.GENERIC_READ | Win32.GENERIC_WRITE;
            var share = Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE;
            var read = Win32.CreateFile(path, rw, share, IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (read.IsInvalid) return null;
            var ctrl = Win32.CreateFile(path, rw, share, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (ctrl.IsInvalid) { read.Dispose(); return null; }

            var wireless = path.Contains("pid_1142", StringComparison.OrdinalIgnoreCase);
            var name = wireless ? $"wireless slot {InterfaceNumber(path)}" : "wired";
            var serial = Win32.GetSerial(ctrl);
            if (serial != null && !wireless) name += $" ({serial})";
            return new ScDevice(path, wireless, name, read, ctrl, caps.InputReportByteLength, caps.FeatureReportByteLength);
        }
    }

    private static string InterfaceNumber(string path)
    {
        var i = path.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 5 <= path.Length ? path.Substring(i + 3, 2) : "?";
    }

    public void Start()
    {
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = $"sc-read {Name}" };
        _thread.Start();
    }

    private void ReadLoop()
    {
        var buffer = GC.AllocateArray<byte>(_inputLength, pinned: true);
        var overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        using var readDone = new ManualResetEvent(false);
        var waits = new WaitHandle[] { readDone, _stop };
        var failed = false;
        try
        {
            fixed (byte* p = buffer)
            {
                while (true)
                {
                    readDone.Reset();
                    *overlapped = default;
                    overlapped->EventHandle = readDone.SafeWaitHandle.DangerousGetHandle();

                    if (!Win32.ReadFile(_readHandle, p, _inputLength, out _, overlapped) &&
                        Marshal.GetLastWin32Error() != Win32.ERROR_IO_PENDING)
                    {
                        failed = true;
                        break;
                    }

                    if (WaitHandle.WaitAny(waits) == 1)
                    {
                        Win32.CancelIoEx(_readHandle, overlapped);
                        Win32.GetOverlappedResult(_readHandle, overlapped, out _, true);
                        break;
                    }

                    if (!Win32.GetOverlappedResult(_readHandle, overlapped, out var n, false))
                    {
                        failed = true;
                        break;
                    }

                    if (n >= 65)
                        ReportReceived?.Invoke(this, buffer.AsMemory(1, n - 1));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Name}] reader crashed: {ex}");
            failed = true;
        }
        finally
        {
            NativeMemory.Free(overlapped);
        }

        if (failed && !_disposed) Closed?.Invoke(this);
    }

    /// <summary>Sends a command as a feature report. Fails harmlessly on an empty dongle slot.</summary>
    public bool SendCommand(ReadOnlySpan<byte> payload)
    {
        var buf = new byte[_featureLength];
        payload.CopyTo(buf.AsSpan(1)); // buf[0] = report ID 0
        lock (_ctrlLock)
        {
            if (_disposed) return false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (Win32.HidD_SetFeature(_ctrlHandle, buf, buf.Length)) return true;
                Thread.Sleep(2);
            }
            return false;
        }
    }

    public bool WriteRegisters(params (byte reg, ushort value)[] regs)
    {
        Span<byte> payload = stackalloc byte[2 + regs.Length * 3];
        payload[0] = ScProtocol.CmdWriteRegister;
        payload[1] = (byte)(regs.Length * 3);
        for (var i = 0; i < regs.Length; i++)
        {
            payload[2 + i * 3] = regs[i].reg;
            BinaryPrimitives.WriteUInt16LittleEndian(payload[(3 + i * 3)..], regs[i].value);
        }
        return SendCommand(payload);
    }

    /// <summary>
    /// "Lizard mode" is the firmware's built-in keyboard/mouse emulation. It must be turned off
    /// while we act as a gamepad, and turned back on when we exit so the controller still works as a mouse.
    /// </summary>
    public bool SetLizardMode(bool enabled)
    {
        if (enabled)
        {
            var ok = SendCommand([ScProtocol.CmdDefaultMappings]);
            return SendCommand([ScProtocol.CmdDefaultMouse]) && ok;
        }

        var cleared = SendCommand([ScProtocol.CmdClearMappings]);
        return WriteRegisters(
            (ScProtocol.RegRPadMode, 0x07),  // right pad: no mouse
            (ScProtocol.RegRPadMargin, 0x00)) && cleared;
    }

    /// <summary>Asks the dongle to report whether a controller is connected to this slot.</summary>
    public void RequestConnectionStatus() => SendCommand([ScProtocol.CmdRequestCommStatus]);

    /// <summary>Repeated haptic pulse: on/off times in microseconds, repeated <paramref name="count"/> times.</summary>
    public void HapticPulse(ScPad pad, ushort onUs, ushort offUs, ushort count)
    {
        Span<byte> p = stackalloc byte[10];
        p[0] = ScProtocol.CmdHapticPulse;
        p[1] = 9;
        p[2] = (byte)pad;
        BinaryPrimitives.WriteUInt16LittleEndian(p[3..], onUs);
        BinaryPrimitives.WriteUInt16LittleEndian(p[5..], offUs);
        BinaryPrimitives.WriteUInt16LittleEndian(p[7..], count);
        p[9] = 0; // gain
        SendCommand(p);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stop.Set();
        if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(1000);
        // A feature write stuck in the driver holds _ctrlLock; don't hang shutdown on it. Without the lock the
        // control handle is left for the process to clean up, and later commands see _disposed and return.
        if (Monitor.TryEnter(_ctrlLock, TimeSpan.FromMilliseconds(500)))
        {
            try
            {
                _disposed = true;
                _ctrlHandle.Dispose();
            }
            finally
            {
                Monitor.Exit(_ctrlLock);
            }
        }
        else
        {
            _disposed = true;
        }
        _readHandle.Dispose();
        _stop.Dispose();
    }
}
