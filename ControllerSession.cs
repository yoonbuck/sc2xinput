using System.Collections.Concurrent;
using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Sc2XInput.Native;
using Sc2XInput.SteamController;

namespace Sc2XInput;

/// <summary>
/// Bridges one Steam Controller to one virtual Xbox 360 controller: the shell around <see cref="SessionCore"/>.
///
/// One loop thread owns the session. The HID reader and ViGEm only post events to its queue; the loop steps
/// the core and carries out its effects (ViGEm calls, lizard mode commands, logging) in order, and waits for
/// the next event or the core's next deadline. Haptic pulses go to a separate worker so HID writes never
/// delay input on its way to the game. Nothing else touches session state, so there are no locks here.
/// </summary>
internal sealed class ControllerSession : IDisposable
{
    private const int QueueWarningDepth = 500;

    private readonly ScDevice _device;
    private readonly ViGEmClient _client;
    private readonly SessionCore _core;
    private readonly ConcurrentQueue<SessionEvent> _queue = new();
    private readonly AutoResetEvent _queued = new(false);
    private readonly ManualResetEvent _closeRequested = new(false);
    private readonly BlockingCollection<Haptic> _haptics = new();
    private readonly Thread _loop;
    private readonly Thread _hapticWorker;
    private volatile SessionStatus _status;

    // Loop thread only.
    private IXbox360Controller? _pad;
    private int _padGeneration;
    private bool _plugFailedInBatch;
    private bool _queueWarned;

    public ScDevice Device => _device;

    /// <summary>Latest snapshot, safe to read from any thread.</summary>
    public SessionStatus Status => _status;

    public bool IsClosed => _status.IsClosed;

    /// <summary>Raised on the loop thread whenever <see cref="Status"/> changes.</summary>
    public event Action? Changed;

    /// <param name="captured">False to start with capture off, e.g. to keep a paused controller paused across a config reload.</param>
    public ControllerSession(ScDevice device, ViGEmClient client, Config config, bool captured = true)
    {
        _device = device;
        _client = client;
        _core = new SessionCore(config, captured, LogFromCore);
        _status = _core.Status;
        _loop = new Thread(Loop) { IsBackground = true, Name = $"session {device.Name}", Priority = ThreadPriority.AboveNormal };
        _hapticWorker = new Thread(RunHaptics) { IsBackground = true, Name = $"haptics {device.Name}" };
        _device.ReportReceived += OnReport;
        _device.Closed += _ => Post(new DeviceLost());
    }

    public void Start()
    {
        _loop.Start();
        _hapticWorker.Start();
        _device.Start();
        // The dongle answers with a connection event (or input reports); a wired controller is just there.
        if (_device.IsWireless) _device.RequestConnectionStatus();
        else Post(new Connected());
    }

    // ---- inputs (other threads) ----

    private void OnReport(ScDevice _, ReadOnlyMemory<byte> report)
    {
        var d = report.Span;
        switch (d[2])
        {
            case ScProtocol.EvConnection when d[4] == ScProtocol.ConnDisconnected:
                Post(new Disconnected());
                break;
            case ScProtocol.EvConnection when d[4] == ScProtocol.ConnConnected:
                Post(new Connected());
                break;
            case ScProtocol.EvInput:
                // The reader reuses its buffer for the next read, so copy.
                Post(new Input(report.ToArray(), Stopwatch.GetTimestamp()));
                break;
        }
    }

    private void Post(SessionEvent e)
    {
        _queue.Enqueue(e);
        _queued.Set();
    }

    // ---- the loop ----

    private void Loop()
    {
        using var wait = new HighResWait($"session {_device.Name}");
        WaitHandle[] handles = [_queued, _closeRequested];
        try
        {
            while (true)
            {
                if (_closeRequested.WaitOne(0)) { Step(new Close()); break; }

                while (_queue.TryDequeue(out var e))
                {
                    Step(e);
                    if (_closeRequested.WaitOne(0) || _core.Status.IsClosed) break;
                }
                WarnIfBackedUp();
                if (_core.NextDeadline is { } due && due <= Stopwatch.GetTimestamp()) Step(new Tick());
                Publish();
                if (_core.Status.IsClosed) break; // DeviceLost

                wait.Wait(handles, _core.NextDeadline);
            }
        }
        catch (Exception ex)
        {
            // A bug in the core or here. Put the controller back the way we found it; the session reports
            // closed, so Bridge replaces it with a fresh one on its next scan.
            Log.Error($"[{_device.Name}] session failed: {ex}");
            Unplug();
            _device.SetLizardMode(true);
            _status = _status with { IsClosed = true };
            Changed?.Invoke();
            return;
        }
        Publish();
    }

    private void Step(SessionEvent e)
    {
        var effects = _core.Step(e, Stopwatch.GetTimestamp());
        // The whole batch runs; failures are stepped back afterwards, in order, before anything else.
        _plugFailedInBatch = false;
        var failures = new List<Effect>();
        foreach (var effect in effects)
            if (!Execute(effect)) failures.Add(effect);
        foreach (var failed in failures) Step(new EffectFailed(failed));
    }

    private void Publish()
    {
        var status = _core.Status;
        if (status == _status) return;
        _status = status;
        Changed?.Invoke();
    }

    private void WarnIfBackedUp()
    {
        if (_queue.Count > QueueWarningDepth && !_queueWarned)
        {
            _queueWarned = true;
            Log.Warn($"[{_device.Name}] session is falling behind ({_queue.Count} events queued)");
        }
        else if (_queue.IsEmpty)
        {
            _queueWarned = false;
        }
    }

    // ---- effects (loop thread) ----

    /// <summary>Carries out one effect; false if it failed and the core should hear about it.</summary>
    private bool Execute(Effect effect)
    {
        switch (effect)
        {
            case PlugPad plug: return Plug(plug.Generation);
            case UnplugPad: Unplug(); return true;
            case Submit submit: return SubmitReport(submit.State);
            case SetLizard lizard: return _device.SetLizardMode(lizard.Enabled);
            case Haptic haptic: _haptics.Add(haptic); return true;
            case QueryPlayerIndex query: QueryPlayer(query.Generation); return true;
            default: throw new InvalidOperationException($"unknown effect {effect}");
        }
    }

    private bool Plug(int generation)
    {
        IXbox360Controller? pad = null;
        try
        {
            pad = _client.CreateXbox360Controller();
            pad.AutoSubmitReport = false;
            pad.FeedbackReceived += (_, e) => Post(new Rumble(generation, e.LargeMotor, e.SmallMotor));
            pad.Connect();
            // Until the driver gets a report that differs from all-zero, XInput reports junk stick values
            // for a new pad. Nudge it so the idle state reads as neutral before any input arrives.
            pad.LeftThumbX = 1;
            pad.SubmitReport();
            pad.LeftThumbX = 0;
            pad.SubmitReport();
        }
        catch (Exception ex)
        {
            Log.Warn($"[{_device.Name}] virtual pad: {ex.Message}");
            try { pad?.Disconnect(); } catch (Exception) { } // don't leave a half-made pad behind
            _plugFailedInBatch = true;
            return false;
        }
        _pad = pad;
        _padGeneration = generation;
        Log.Info($"[{_device.Name}] -> virtual Xbox 360 pad");
        return true;
    }

    private void Unplug()
    {
        if (_pad == null) return;
        try { _pad.Disconnect(); }
        catch (Exception ex) { Log.Warn($"[{_device.Name}] virtual pad disconnect failed: {ex.Message}"); }
        _pad = null;
    }

    private bool SubmitReport(XState x)
    {
        if (_pad == null) return _plugFailedInBatch; // skipped: the plug it was for just failed
        try
        {
            _pad.SetButtonsFull((ushort)x.Buttons);
            _pad.LeftTrigger = x.LeftTrigger;
            _pad.RightTrigger = x.RightTrigger;
            _pad.LeftThumbX = x.LeftX;
            _pad.LeftThumbY = x.LeftY;
            _pad.RightThumbX = x.RightX;
            _pad.RightThumbY = x.RightY;
            _pad.SubmitReport();
            return true;
        }
        catch (Exception)
        {
            return false; // the core logs and retries
        }
    }

    // XInput assigns the player slot asynchronously after Connect; the core polls until it has one.
    private void QueryPlayer(int generation)
    {
        int? index = null;
        if (_pad != null && generation == _padGeneration)
        {
            try { index = _pad.UserIndex; }
            catch (Exception) { } // not assigned yet
        }
        Post(new PlayerIndexResult(generation, index));
    }

    private void LogFromCore(LogLevel level, string text)
    {
        var line = $"[{_device.Name}] {text}";
        switch (level)
        {
            case LogLevel.Warn: Log.Warn(line); break;
            case LogLevel.Error: Log.Error(line); break;
            default: Log.Info(line); break;
        }
    }

    // ---- haptics (worker thread) ----

    private void RunHaptics()
    {
        try
        {
            foreach (var h in _haptics.GetConsumingEnumerable()) _device.HapticPulse(h.Pad, h.OnUs, h.OffUs, h.Count);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // shutting down
        }
    }

    // ---- shutdown ----

    /// <summary>Asks the loop to unplug and restore mouse mode, without waiting. See <see cref="Dispose(TimeSpan)"/>.</summary>
    public void RequestClose() => _closeRequested.Set();

    public void Dispose() => Dispose(TimeSpan.FromSeconds(2));

    /// <summary>
    /// Closes the session: the loop unplugs the pad and restores mouse mode, then the device is closed.
    /// Waits up to <paramref name="timeout"/> for the loop (Windows allows ~5 s for logoff/console close in
    /// total, and Bridge closes all sessions in parallel within that).
    /// </summary>
    public void Dispose(TimeSpan timeout)
    {
        RequestClose();
        if (_loop.IsAlive && Thread.CurrentThread != _loop && !_loop.Join(timeout))
            Log.Warn($"[{_device.Name}] session didn't close in time; run sc2xinput --restore if mouse mode is off");
        _haptics.CompleteAdding();
        if (_hapticWorker.IsAlive && Thread.CurrentThread != _hapticWorker) _hapticWorker.Join(TimeSpan.FromMilliseconds(200));
        _device.Dispose();
    }
}
