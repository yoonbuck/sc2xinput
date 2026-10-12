using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sc2XInput.Native;

/// <summary>
/// Waits for any of a set of handles or a deadline, whichever comes first, with ~1 ms precision. Uses a
/// high-resolution waitable timer: plain timeouts round to ~15.6 ms, and Windows 11 ignores timeBeginPeriod
/// for processes without a visible window (like a tray app). Falls back to plain timeouts if the timer
/// can't be created or armed. Not thread-safe; owned by one loop.
/// </summary>
internal sealed class HighResWait : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int periodMs, IntPtr completion, IntPtr arg, bool resume);

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle h) => SafeWaitHandle = h;
    }

    private readonly string _name;
    private readonly TimerHandle? _timer;
    private int _armFailures;

    public HighResWait(string name)
    {
        _name = name;
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (handle.IsInvalid) // pre-1803 Windows: fall back to a normal-resolution timer
        {
            handle.Dispose();
            handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
        }
        if (handle.IsInvalid)
        {
            Log.Warn($"{name}: could not create a timer (error {Marshal.GetLastPInvokeError()}); timing at reduced resolution");
            handle.Dispose();
            return;
        }
        _timer = new TimerHandle(handle);
    }

    /// <summary>
    /// Blocks until one of <paramref name="handles"/> is signalled (returns its index) or
    /// <paramref name="deadline"/> (Stopwatch ticks) passes (returns -1). No deadline waits for a handle only.
    /// </summary>
    public int Wait(WaitHandle[] handles, long? deadline)
    {
        if (deadline is not { } at) return WaitHandle.WaitAny(handles);
        var due = at - Stopwatch.GetTimestamp();
        if (due <= 0) return -1;

        if (_timer != null && SetWaitableTimer(_timer.SafeWaitHandle, -(due * 10_000_000 / Stopwatch.Frequency), 0,
                IntPtr.Zero, IntPtr.Zero, false))
        {
            if (_armFailures > 0)
            {
                Log.Info($"{_name}: timer armed again after {_armFailures} failures");
                _armFailures = 0;
            }
            var index = WaitHandle.WaitAny([.. handles, _timer]);
            return index == handles.Length ? -1 : index;
        }

        if (_timer != null && _armFailures++ == 0)
            Log.Warn($"{_name}: could not arm timer (error {Marshal.GetLastPInvokeError()}); timing at reduced resolution");
        var ms = (int)Math.Clamp((due * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency, 1, int.MaxValue);
        var result = WaitHandle.WaitAny(handles, ms);
        return result == WaitHandle.WaitTimeout ? -1 : result;
    }

    public void Dispose() => _timer?.Dispose();
}
