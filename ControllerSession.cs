using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Sc2XInput.Native;
using Sc2XInput.SteamController;

namespace Sc2XInput;

/// <summary>Bridges one Steam Controller to one virtual Xbox 360 controller.</summary>
internal sealed class ControllerSession : IDisposable
{
    private const int RumbleTickMs = 50;
    private const ushort LargeMotorPeriodUs = 10_000; // ~100 Hz thump on the left pad
    private const ushort SmallMotorPeriodUs = 4_000;  // ~250 Hz buzz on the right pad
    private const int CameraTickMs = 4; // camera output can change between reports (inertia, fade-out when reports stop)
    // Physical buttons; excludes the touch/status bits that share the field.
    private const ScButtons AnyButton = (ScButtons)0x7FFFFF & ~(ScButtons.LPadTouch | ScButtons.RPadTouch);

    private readonly ScDevice _device;
    private readonly ViGEmClient _client;
    private readonly Config _config;
    private readonly ScInputState _state = new();
    private readonly object _lock = new();
    private readonly System.Threading.Timer? _rumbleTimer;
    private readonly System.Threading.Timer _chordTimer;
    private readonly CameraPad _camera;
    private readonly SteamChords _chords;
    private HighResTicker? _cameraTicker; // created when first needed and paused while idle or disconnected
    private IXbox360Controller? _pad;
    private bool _present;        // a physical controller is connected (always true when wired)
    private bool _captured;       // false: the user handed the controller back to mouse/keyboard mode
    private bool _lizardPending;  // capture turned off; restore mouse/keyboard mode once all buttons are up
    private long _lizardRetryAt;  // after a failed restore, don't retry before this timestamp
    private int _lizardFailures;
    private bool _rightPadMuted;  // right pad ignored until the thumb lifts, after switching its mode
    private RightPadMode _rightPadMode;
    private XState _mapped; // latest mapping of the physical controls; camera output is layered on top
    private XState _last;
    private volatile byte _largeMotor, _smallMotor;

    public ScDevice Device => _device;
    public bool IsClosed { get; private set; }
    public bool HasController => _present;
    public bool IsCaptured => _captured;
    public RightPadMode RightPadMode => _rightPadMode;
    /// <summary>Zero-based XInput slot, once Windows has assigned one.</summary>
    public int? PlayerIndex { get; private set; }

    /// <summary>Raised when the controller connects, disconnects, gets its player slot or changes mode.</summary>
    public event Action? Changed;

    /// <param name="captured">False to start with capture off, e.g. to keep a paused controller paused across a config reload.</param>
    public ControllerSession(ScDevice device, ViGEmClient client, Config config, bool captured = true)
    {
        _device = device;
        _client = client;
        _config = config;
        _captured = captured;
        _rightPadMode = config.RightPadMode;
        _camera = new CameraPad(config);
        _chords = new SteamChords(config.SteamChords);
        _chordTimer = new System.Threading.Timer(_ => OnChordTimer());
        _device.ReportReceived += OnReport;
        _device.Closed += OnClosed;
        if (config.Rumble)
            _rumbleTimer = new System.Threading.Timer(_ => RumbleTick(), null, RumbleTickMs, RumbleTickMs);
    }

    public void Start()
    {
        _device.Start();
        if (_device.IsWireless)
            _device.RequestConnectionStatus(); // answer arrives as a connection event (or input reports)
        else
            OnControllerConnected();
    }

    private void OnReport(ScDevice _, ReadOnlyMemory<byte> report)
    {
        var d = report.Span;
        switch (d[2])
        {
            case ScProtocol.EvConnection when d[4] == ScProtocol.ConnDisconnected:
                OnControllerDisconnected();
                break;
            case ScProtocol.EvConnection when d[4] == ScProtocol.ConnConnected:
                OnControllerConnected();
                break;
            case ScProtocol.EvInput:
                if (!_present) OnControllerConnected();
                lock (_lock)
                {
                    // Reports keep arriving until Dispose closes the device; don't act on them after that.
                    if (IsClosed) return;
                    _state.Apply(d);
                    _camera.Update(_state.RPadTouched, _state.RPadX, _state.RPadY, Stopwatch.GetTimestamp());
                    // Wake the ticker if CameraTick paused it while the pad was idle.
                    if (_rightPadMode == RightPadMode.Camera && _pad != null && _camera.IsActive(Stopwatch.GetTimestamp()))
                        _cameraTicker?.Enabled = true;
                    Remap();
                }
                Submit();
                break;
        }
    }

    // Caller holds _lock. Chords are processed even while not captured, so the toggle chord can recapture.
    private void Remap()
    {
        var now = Stopwatch.GetTimestamp();
        var chords = _chords.Process(_state.Buttons, now);
        foreach (var action in chords.Actions) Run(action);
        if (_rightPadMuted && !_state.RPadTouched)
        {
            _rightPadMuted = false;
            _camera.Reset(); // or a trackball flick from the muted touch would coast on after the lift
        }
        _mapped = Mapper.Map(_state, chords.Buttons, _config, _rightPadMuted ? RightPadMode.None : _rightPadMode);
        // Enabling mouse/keyboard mode while a button is held makes the firmware type that button's key
        // (Back is Tab), so wait until the chord has been fully released. It stays pending until the
        // commands go through; a failing attempt costs several retries, so space them out.
        if (_lizardPending && (_state.Buttons & AnyButton) == 0 && now >= _lizardRetryAt)
        {
            if (_device.SetLizardMode(true))
            {
                _lizardPending = false;
                _lizardFailures = 0;
            }
            else
            {
                if (_lizardFailures++ == 0)
                    Log.Warn($"[{_device.Name}] could not restore mouse/keyboard mode, retrying");
                _lizardRetryAt = now + Stopwatch.Frequency / 2;
            }
        }
        if (chords.WakeAt is { } wake)
            _chordTimer.Change(Math.Max(1, (wake - now) * 1000 / Stopwatch.Frequency + 1), Timeout.Infinite);
    }

    private void OnChordTimer()
    {
        // Timer callbacks have no outer handler, and an exception here would end the process; Submit can
        // throw if ViGEm fails (e.g. the bus driver restarted).
        try
        {
            lock (_lock)
            {
                if (IsClosed) return;
                Remap();
            }
            Submit();
        }
        catch (Exception ex)
        {
            Log.Error($"[{_device.Name}] chord timer failed: {ex.Message}");
        }
    }

    // Caller holds _lock.
    private void Run(ChordAction action)
    {
        switch (action)
        {
            case ChordAction.ToggleCapture when _captured:
                _captured = false;
                DisconnectPad();
                _lizardPending = true;
                _lizardRetryAt = 0;
                Log.Info($"[{_device.Name}] capture off: controller is back in mouse/keyboard mode");
                Feedback(2, ScPad.Left, ScPad.Right);
                Changed?.Invoke();
                break;
            case ChordAction.ToggleCapture:
                _captured = true;
                _lizardPending = false;
                ConnectPad();
                Log.Info($"[{_device.Name}] capture on");
                Feedback(1, ScPad.Left, ScPad.Right);
                break;
            case ChordAction.CycleRightPadMode when !_captured:
                break; // nothing to switch while the controller is in mouse/keyboard mode
            case ChordAction.CycleRightPadMode:
                // Camera -> Stick -> Camera, passing through None only if that's what the config asks for.
                _rightPadMode = _rightPadMode switch
                {
                    RightPadMode.Camera => RightPadMode.Stick,
                    RightPadMode.Stick when _config.RightPadMode == RightPadMode.None => RightPadMode.None,
                    _ => RightPadMode.Camera,
                };
                // The chord is a click with the thumb on the pad; in Stick mode that position would
                // immediately deflect the stick, so ignore the pad until the thumb lifts.
                _rightPadMuted = _state.RPadTouched;
                if (_rightPadMode == RightPadMode.Camera && _pad != null) EnsureCameraTicker();
                else _cameraTicker?.Enabled = false;
                Log.Info($"[{_device.Name}] right pad mode: {_rightPadMode}");
                Feedback(_rightPadMode switch { RightPadMode.Camera => 1, RightPadMode.Stick => 2, _ => 3 }, ScPad.Right);
                Changed?.Invoke();
                break;
        }
    }

    /// <summary>Confirmation buzz: <paramref name="ticks"/> short pulses on the given pads.</summary>
    // Delayed so it isn't masked by the physical click of the chord button.
    private void Feedback(int ticks, params ScPad[] pads) => _ = Task.Run(async () =>
    {
        for (var i = 0; i < ticks; i++)
        {
            await Task.Delay(i == 0 ? 250 : 200);
            foreach (var pad in pads) _device.HapticPulse(pad, 2000, 2000, 10); // 40 ms at 250 Hz
        }
    });

    // Between reports the camera output only changes for trackball inertia or the fade-out when reports
    // stop, so pause once the pad is idle and the stick has been centred. The next touch wakes it.
    private void CameraTick()
    {
        Submit();
        lock (_lock)
        {
            if (!_camera.IsActive(Stopwatch.GetTimestamp()) && _last.RightX == 0 && _last.RightY == 0)
                _cameraTicker?.Enabled = false;
        }
    }

    private void Submit()
    {
        lock (_lock)
        {
            if (_pad == null) return;
            var x = _mapped;
            if (_rightPadMode == RightPadMode.Camera && !_rightPadMuted)
            {
                var (rx, ry) = _camera.Sample(Stopwatch.GetTimestamp());
                x = x with { RightX = rx, RightY = ry };
            }
            if (x == _last) return;
            _pad.SetButtonsFull((ushort)x.Buttons);
            _pad.LeftTrigger = x.LeftTrigger;
            _pad.RightTrigger = x.RightTrigger;
            _pad.LeftThumbX = x.LeftX;
            _pad.LeftThumbY = x.LeftY;
            _pad.RightThumbX = x.RightX;
            _pad.RightThumbY = x.RightY;
            _pad.SubmitReport();
            _last = x; // only once sent, so a failed submit is retried on the next tick
        }
    }

    private void OnControllerConnected()
    {
        lock (_lock)
        {
            if (_present || IsClosed) return;
            _present = true;
            Log.Info($"[{_device.Name}] controller connected");
            if (_captured) ConnectPad();
            else Log.Info($"[{_device.Name}] capture is off, leaving it in mouse/keyboard mode");
            Changed?.Invoke();
        }
    }

    // Caller holds _lock.
    private void ConnectPad()
    {
        if (_pad != null || IsClosed) return;
        if (!_device.SetLizardMode(false))
            Log.Warn($"[{_device.Name}] could not disable lizard mode; trackpad may still move the mouse");

        var pad = _client.CreateXbox360Controller();
        pad.AutoSubmitReport = false;
        pad.FeedbackReceived += (_, e) => { _largeMotor = e.LargeMotor; _smallMotor = e.SmallMotor; };
        pad.Connect();
        // Until the driver gets a report that differs from all-zero, XInput reports junk stick values
        // for a new pad. Nudge it so the idle state reads as neutral before any input arrives.
        pad.LeftThumbX = 1;
        pad.SubmitReport();
        pad.LeftThumbX = 0;
        pad.SubmitReport();
        _pad = pad;
        _last = default;
        _mapped = default; // don't replay input from before a disconnect
        _camera.Reset(); // nor trackpad motion from mouse mode (e.g. trackball inertia)
        EnsureCameraTicker();

        Log.Info($"[{_device.Name}] -> virtual Xbox 360 pad");
        Changed?.Invoke();
        _ = LogPlayerIndexAsync(pad);
    }

    private void EnsureCameraTicker()
    {
        if (_rightPadMode != RightPadMode.Camera) return;
        try
        {
            _cameraTicker ??= new HighResTicker(CameraTickMs, CameraTick, $"camera {_device.Name}");
            _cameraTicker.Enabled = true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Camera output still updates on every report; it just won't settle between them.
            Log.Warn($"[{_device.Name}] {ex.Message}; camera mode will only update on input reports");
        }
    }

    // XInput assigns the player slot asynchronously after Connect().
    private async Task LogPlayerIndexAsync(IXbox360Controller pad)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100);
            if (_pad != pad) return;
            try
            {
                PlayerIndex = pad.UserIndex;
                Log.Info($"[{_device.Name}] virtual pad is XInput player {PlayerIndex + 1}");
                Changed?.Invoke();
                return;
            }
            catch (Exception) { }
        }
    }

    private void OnControllerDisconnected()
    {
        lock (_lock)
        {
            if (!_present) return;
            _present = false;
            DisconnectPad();
            // Buttons held when it dropped out are not still held when it wakes.
            _chords.Reset();
            _camera.Reset(); // also when capture was off, so there was no pad to disconnect
            _rightPadMuted = false;
            Log.Info($"[{_device.Name}] controller disconnected");
            Changed?.Invoke();
        }
    }

    // Caller holds _lock.
    private void DisconnectPad()
    {
        if (_pad == null) return;
        _largeMotor = _smallMotor = 0;
        try { _pad.Disconnect(); }
        catch (Exception ex) { Log.Warn($"[{_device.Name}] virtual pad disconnect failed: {ex.Message}"); }
        _pad = null;
        PlayerIndex = null;
        _camera.Reset(); // so a link drop mid-swipe or mid-coast doesn't resume with stale motion
        _cameraTicker?.Enabled = false; // a sleeping wireless controller shouldn't keep a 4 ms timer going
        Changed?.Invoke();
    }

    private void OnClosed(ScDevice _)
    {
        lock (_lock)
        {
            if (_present) Log.Info($"[{_device.Name}] device removed");
            _present = false;
            DisconnectPad();
            IsClosed = true;
            Changed?.Invoke(); // DisconnectPad doesn't notify when capture was off and there was no pad
        }
    }

    // XInput rumble is a level ("motor at 60%") that games set and forget, but the Steam Controller
    // only does finite pulse trains, so keep re-issuing short trains while a motor is on.
    private void RumbleTick()
    {
        if (_pad == null) return;
        Drive(ScPad.Left, _largeMotor, LargeMotorPeriodUs);
        Drive(ScPad.Right, _smallMotor, SmallMotorPeriodUs);
    }

    private void Drive(ScPad pad, byte level, ushort periodUs)
    {
        if (level == 0) return;
        var duty = Math.Clamp(level / 255.0 * _config.RumbleStrength, 0, 1);
        var on = (ushort)Math.Max(1, periodUs / 2 * duty);
        var off = (ushort)(periodUs - on);
        var count = (ushort)Math.Ceiling(RumbleTickMs * 1000 * 1.5 / periodUs); // overlap the next tick slightly
        _device.HapticPulse(pad, on, off, count);
    }

    public void Dispose()
    {
        _rumbleTimer?.Dispose();
        bool hadController;
        lock (_lock)
        {
            hadController = _pad != null || _lizardPending;
            DisconnectPad();
            _present = false;
            IsClosed = true; // from here on, reports and timer callbacks are ignored
        }
        // After IsClosed is set under the lock, so a report already inside Remap can't re-arm it once
        // disposed (Timer.Change is documented to throw ObjectDisposedException then).
        _chordTimer.Dispose();
        // After DisconnectPad (which pauses the ticker) so it never touches a disposed one, and outside
        // the lock because the tick takes it and Dispose joins the thread.
        _cameraTicker?.Dispose();
        // Give the controller its mouse/keyboard behavior back so it is usable without us.
        if (hadController) _device.SetLizardMode(true);
        _device.Dispose();
    }
}
