using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sc2XInput.Native;

/// <summary>
/// Runs a callback at a fixed millisecond interval on a dedicated thread. Uses a high-resolution waitable
/// timer: plain timers tick at ~15.6 ms, and Windows 11 ignores timeBeginPeriod for processes without a
/// visible window (like a tray app).
/// </summary>
internal sealed class HighResTicker : IDisposable
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

    private readonly ManualResetEvent _stop = new(false);
    private readonly ManualResetEvent _enabled = new(true);
    private readonly Thread _thread;

    /// <summary>
    /// While false the thread parks without timer wakeups. Never blocks, so it is safe to set from code
    /// holding a lock that the tick callback also takes.
    /// </summary>
    public bool Enabled
    {
        get => _enabled.WaitOne(0);
        set { if (value) _enabled.Set(); else _enabled.Reset(); }
    }

    public HighResTicker(int intervalMs, Action tick, string name)
    {
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (handle.IsInvalid) // pre-1803 Windows: fall back to a normal-resolution timer
        {
            handle.Dispose();
            handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
            if (handle.IsInvalid)
            {
                // Fail here rather than on the new thread, where an exception would take down the process.
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new System.ComponentModel.Win32Exception(error, $"{name}: could not create a timer");
            }
        }
        var timer = new TimerHandle(handle);

        _thread = new Thread(() =>
        {
            using (timer)
            {
                var waits = new WaitHandle[] { timer, _stop };
                var pausedWaits = new WaitHandle[] { _enabled, _stop };
                var due = -10_000L * intervalMs; // relative, in 100 ns units
                var failures = 0;
                while (true)
                {
                    SetWaitableTimer(handle, due, 0, IntPtr.Zero, IntPtr.Zero, false);
                    if (WaitHandle.WaitAny(waits) == 1) return;
                    if (!_enabled.WaitOne(0) && WaitHandle.WaitAny(pausedWaits) == 1) return;
                    try
                    {
                        tick();
                        if (failures > 0)
                        {
                            Log.Info($"{name} recovered after {failures} failed ticks");
                            failures = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log only the first of a run: at this rate a persistent error would flood the log.
                        // Keep ticking, since the cause may be transient.
                        if (failures++ == 0) Log.Error($"{name} tick failed (repeats suppressed until it recovers): {ex}");
                    }
                }
            }
        }) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
        _stop.Dispose();
        _enabled.Dispose();
    }
}
