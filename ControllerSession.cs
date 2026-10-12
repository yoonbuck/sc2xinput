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
    private const int CameraTickMs = 4; // camera output keeps changing between reports (smoothing, inertia)

    private readonly ScDevice _device;
    private readonly ViGEmClient _client;
    private readonly Config _config;
    private readonly ScInputState _state = new();
    private readonly object _lock = new();
    private readonly System.Threading.Timer? _rumbleTimer;
    private readonly CameraPad? _camera;
    private HighResTicker? _cameraTicker; // started on first connect, so empty dongle slots don't tick
    private IXbox360Controller? _pad;
    private XState _mapped; // latest mapping of the physical controls; camera output is layered on top
    private XState _last;
    private volatile byte _largeMotor, _smallMotor;

    public ScDevice Device => _device;
    public bool IsClosed { get; private set; }
    public bool HasController => _pad != null;
    /// <summary>Zero-based XInput slot, once Windows has assigned one.</summary>
    public int? PlayerIndex { get; private set; }

    /// <summary>Raised when the controller connects, disconnects or gets its player slot.</summary>
    public event Action? Changed;

    public ControllerSession(ScDevice device, ViGEmClient client, Config config)
    {
        _device = device;
        _client = client;
        _config = config;
        _device.ReportReceived += OnReport;
        _device.Closed += OnClosed;
        if (config.Rumble)
            _rumbleTimer = new System.Threading.Timer(_ => RumbleTick(), null, RumbleTickMs, RumbleTickMs);
        if (config.RightPadMode == RightPadMode.Camera)
            _camera = new CameraPad(config);
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
                if (_pad == null) OnControllerConnected();
                _state.Apply(d);
                _camera?.Update(_state.RPadTouched, _state.RPadX, _state.RPadY, Stopwatch.GetTimestamp());
                lock (_lock) _mapped = Mapper.Map(_state, _config);
                Submit();
                break;
        }
    }

    private void Submit()
    {
        lock (_lock)
        {
            if (_pad == null) return;
            var x = _mapped;
            if (_camera != null)
            {
                var (rx, ry) = _camera.Sample(Stopwatch.GetTimestamp());
                x = x with { RightX = rx, RightY = ry };
            }
            if (x == _last) return;
            _last = x;
            _pad.SetButtonsFull((ushort)x.Buttons);
            _pad.LeftTrigger = x.LeftTrigger;
            _pad.RightTrigger = x.RightTrigger;
            _pad.LeftThumbX = x.LeftX;
            _pad.LeftThumbY = x.LeftY;
            _pad.RightThumbX = x.RightX;
            _pad.RightThumbY = x.RightY;
            _pad.SubmitReport();
        }
    }

    private void OnControllerConnected()
    {
        lock (_lock)
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
            _mapped = default;
            if (_camera != null)
                _cameraTicker ??= new HighResTicker(CameraTickMs, Submit, $"camera {_device.Name}");

            Log.Info($"[{_device.Name}] controller connected -> virtual Xbox 360 pad");
            Changed?.Invoke();
            _ = LogPlayerIndexAsync(pad);
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
            if (_pad == null) return;
            DisconnectPad();
            Log.Info($"[{_device.Name}] controller disconnected");
        }
    }

    private void DisconnectPad()
    {
        if (_pad == null) return;
        _largeMotor = _smallMotor = 0;
        try { _pad.Disconnect(); }
        catch (Exception ex) { Log.Warn($"[{_device.Name}] virtual pad disconnect failed: {ex.Message}"); }
        _pad = null;
        PlayerIndex = null;
        Changed?.Invoke();
    }

    private void OnClosed(ScDevice _)
    {
        lock (_lock)
        {
            if (_pad != null) Log.Info($"[{_device.Name}] device removed");
            DisconnectPad();
            IsClosed = true;
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
        _cameraTicker?.Dispose();
        _rumbleTimer?.Dispose();
        bool hadController;
        lock (_lock)
        {
            hadController = _pad != null;
            DisconnectPad();
            IsClosed = true;
        }
        // Give the controller its mouse/keyboard behavior back so it is usable without us.
        if (hadController) _device.SetLizardMode(true);
        _device.Dispose();
    }
}
