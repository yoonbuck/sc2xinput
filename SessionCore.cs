using System.Diagnostics;
using Sc2XInput.SteamController;

namespace Sc2XInput;

/// <summary>
/// The logic for one controller, with no I/O, threads or clock: <see cref="Step"/> takes an event and the
/// current time and returns the effects for the shell to carry out, and <see cref="NextDeadline"/> says when
/// it needs a <see cref="Tick"/>. Everything that used to run on timers is a deadline here.
///
/// Lifecycle: Asleep → Gamepad (captured) / Mouse (capture off) → … → Closed. Input-derived state lives in
/// <see cref="InputTracker"/> (replaced only when the link drops) and <see cref="OutputStage"/> (exists only
/// in Gamepad, replaced when the pad is plugged in or the right-pad mode changes), so a transition can't
/// leave stale state behind by forgetting to reset a field.
/// </summary>
internal sealed class SessionCore
{
    public abstract record Lifecycle;

    /// <summary>No controller on the link. <paramref name="LizardMayBeOff"/>: it dropped mid-restore.</summary>
    public sealed record Asleep(bool Captured, bool LizardMayBeOff) : Lifecycle;

    /// <summary>Captured. <paramref name="PadPlugged"/> is false while waiting to retry a failed plug.</summary>
    public sealed record Gamepad(bool PadPlugged, long PlugRetryAt) : Lifecycle;

    /// <summary>Capture off; mouse mode is restored once every button is up and <paramref name="RetryAt"/> has passed.</summary>
    public sealed record Releasing(long RetryAt) : Lifecycle;

    /// <summary>Capture off and the controller is in its own mouse/keyboard mode. Only chords are processed.</summary>
    public sealed record Mouse : Lifecycle;

    public sealed record Closed : Lifecycle;

    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    private static readonly long OutputTick = Ms(4);         // camera inertia / fade-out, submit retry
    private static readonly long RumbleTick = Ms(50);        // re-issue pulse trains while a motor is on
    private static readonly long FeedbackDelay = Ms(250);    // after the chord, so the button's click doesn't mask it
    private static readonly long FeedbackSpacing = Ms(200);
    private static readonly long LizardRetry = Ms(500);
    private static readonly long PlugBackoffMin = Ms(500);
    private static readonly long PlugBackoffMax = Ms(8000);
    private static readonly long PlayerPollInterval = Ms(100);
    private const int PlayerPolls = 20;
    private const int SubmitFailuresBeforeReplug = 50;
    // Rumble, tuned on hardware. Each trackpad actuator has resonances: 80 Hz is the strongest low "thump" and
    // 200 Hz a strong buzz, while 40-60 Hz is weak and 120 Hz barely registers. Strength comes from the on-time
    // of each pulse, barely felt at ~100 µs and plateauing at 1.5-2 ms (and weaker near 50% duty), so level maps
    // geometrically onto a range that stays noticeable. Both periods divide RumbleTick, so a re-issued train
    // roughly continues the rhythm; trains overlap, so timing jitter leaves no gap.
    private static readonly RumbleMotor LargeMotor = new(PeriodUs: 12_500, MinOnUs: 250, MaxOnUs: 2000); // 80 Hz
    private static readonly RumbleMotor SmallMotor = new(PeriodUs: 5_000, MinOnUs: 250, MaxOnUs: 1500);  // 200 Hz

    // Physical buttons; excludes the touch/status bits that share the field.
    private const ScButtons AnyButton = (ScButtons)0x7FFFFF & ~(ScButtons.LPadTouch | ScButtons.RPadTouch);

    private readonly Config _config;
    private readonly Action<LogLevel, string> _log;
    private readonly List<Effect> _effects = [];
    private readonly List<(long At, Haptic Effect)> _feedback = [];

    private Lifecycle _life;
    private RightPadMode _mode;
    private InputTracker _input;
    private OutputStage? _output;
    private int _padGeneration;
    private int? _player;
    private long? _playerPollAt;
    private int _playerPollsLeft;
    private byte _largeMotor, _smallMotor;
    private long? _rumbleAt;
    private long? _outputAt;
    private long _plugBackoff = PlugBackoffMin;
    private int _submitFailures, _lizardFailures;
    // Whether the previous step emitted these. The shell steps a batch's failures right after it, so if the
    // next other event arrives with the flag still set, the effect succeeded.
    private bool _submitPending, _restorePending;

    /// <param name="captured">False to start with capture off (keeps a paused controller paused across a reload).</param>
    /// <param name="lizardMayBeOff">The controller may have been left without mouse mode (e.g. a replaced session
    /// failed to restore it); with capture off, restore it on wake.</param>
    public SessionCore(Config config, bool captured = true, Action<LogLevel, string>? log = null, bool lizardMayBeOff = false)
    {
        _config = config;
        _log = log ?? ((_, _) => { });
        _life = new Asleep(captured, lizardMayBeOff);
        _mode = config.RightPadMode;
        _input = new InputTracker(config);
    }

    public Lifecycle State => _life;

    public SessionStatus Status => new(
        Present: _life is Gamepad or Releasing or Mouse,
        Captured: _life is Gamepad or Asleep { Captured: true },
        IsClosed: _life is Closed,
        Player: _player,
        Mode: _mode);

    /// <summary>When the core needs a <see cref="Tick"/>; null when it's idle until the next event.</summary>
    public long? NextDeadline
    {
        get
        {
            if (_life is Closed) return null;
            long? next = null;
            void Consider(long? at) { if (at is { } t && (next is not { } n || t < n)) next = t; }
            Consider(_input.WakeAt);
            Consider(_outputAt);
            Consider(_rumbleAt);
            Consider(_playerPollAt);
            foreach (var f in _feedback) Consider(f.At);
            if (_life is Gamepad { PadPlugged: false, PlugRetryAt: var plugAt }) Consider(plugAt);
            if (_life is Releasing { RetryAt: var retryAt } && AllButtonsUp) Consider(retryAt);
            return next;
        }
    }

    public IReadOnlyList<Effect> Step(SessionEvent e, long now)
    {
        _effects.Clear();
        if (_life is Closed) return [];
        ConfirmPreviousEffects(e);

        switch (e)
        {
            case Close: Shutdown(); break;
            case DeviceLost: Lost(); break;
            case Connected when _life is Asleep asleep: Wake(asleep, now); break;
            case Connected: break;
            case Disconnected: LinkLost(); break;
            case Input input:
                if (_life is Asleep sleeping) Wake(sleeping, now);
                OnInput(input, now);
                break;
            case Tick: OnTick(now); break;
            case Rumble rumble: OnRumble(rumble, now); break;
            case PlayerIndexResult result: OnPlayerIndex(result, now); break;
            case EffectFailed failed: OnFailed(failed.Effect, now); break;
        }
        return _effects.ToArray();
    }

    // The shell steps all of a batch's failures right after it, before any other event. So a failure clears
    // its pending flag, and the first other event confirms whatever is still pending.
    private void ConfirmPreviousEffects(SessionEvent e)
    {
        // Close may be stepped ahead of a failure (it bypasses the queue), so it confirms nothing; Shutdown
        // treats a still-unconfirmed restore as possibly failed.
        if (e is Close) return;
        if (e is EffectFailed failed)
        {
            if (failed.Effect is Submit) _submitPending = false;
            if (failed.Effect is SetLizard { Enabled: true }) _restorePending = false;
            return;
        }
        if (_submitPending)
        {
            if (_submitFailures > 0) _log(LogLevel.Info, $"virtual pad accepting reports again after {_submitFailures} failures");
            _submitFailures = 0;
            // Reset only once the pad actually takes a report: a pad that plugs fine but rejects every
            // report would otherwise be re-created every ~0.7 s forever.
            _plugBackoff = PlugBackoffMin;
        }
        if (_restorePending) _lizardFailures = 0;
        _submitPending = _restorePending = false;
    }

    // Unknown until the first report on this link, e.g. right after reconnecting.
    private bool AllButtonsUp => _input.HasInput && (_input.State.Buttons & AnyButton) == 0;

    private void Emit(Effect effect)
    {
        _effects.Add(effect);
        if (effect is Submit) _submitPending = true;
        if (effect is SetLizard { Enabled: true }) _restorePending = true;
    }

    // ---- lifecycle transitions ----

    private void Wake(Asleep asleep, long now)
    {
        _log(LogLevel.Info, "controller connected");
        if (asleep.Captured)
        {
            EnterGamepad(now);
            return;
        }
        _log(LogLevel.Info, "capture is off, leaving it in mouse/keyboard mode");
        // If it dropped out before mouse mode was restored, finish that now.
        _life = asleep.LizardMayBeOff ? new Releasing(RetryAt: 0) : new Mouse();
    }

    private void EnterGamepad(long now)
    {
        // Always, even coming from Releasing: a failed restore can leave part of mouse mode applied.
        Emit(new SetLizard(false));
        Plug(now);
    }

    private void Plug(long now)
    {
        _padGeneration++;
        Emit(new PlugPad(_padGeneration));
        _life = new Gamepad(PadPlugged: true, PlugRetryAt: 0);
        _output = new OutputStage(_config, _mode, muted: false);
        _player = null;
        _playerPollsLeft = PlayerPolls;
        _playerPollAt = now + PlayerPollInterval;
        _submitFailures = 0;
        RefreshOutput(now);
    }

    private void Unplug()
    {
        if (_life is Gamepad { PadPlugged: true }) Emit(new UnplugPad());
        _player = null;
        _playerPollAt = null;
        _largeMotor = _smallMotor = 0;
        _rumbleAt = null;
        _outputAt = null;
    }

    private void LinkLost()
    {
        if (_life is Asleep) return;
        var next = _life switch
        {
            Gamepad => new Asleep(Captured: true, LizardMayBeOff: false),
            Releasing => new Asleep(Captured: false, LizardMayBeOff: true),
            _ => new Asleep(Captured: false, LizardMayBeOff: false),
        };
        Unplug(); // looks at the state being left
        _life = next;
        ResetForLinkLoss();
        _log(LogLevel.Info, "controller disconnected");
    }

    private void Lost()
    {
        Unplug();
        _life = new Closed();
        ResetForLinkLoss();
    }

    private void Shutdown()
    {
        // Give the controller its mouse/keyboard mode back if it might be off, even with buttons held.
        // Includes a restore emitted in the previous step whose outcome isn't known yet.
        var lizardMayBeOff = _life is Gamepad or Releasing or Asleep { LizardMayBeOff: true } || _restorePending;
        Unplug();
        if (lizardMayBeOff) Emit(new SetLizard(true));
        _life = new Closed();
        ResetForLinkLoss();
    }

    // Nothing scheduled or computed from the old input stream survives, only which buttons were held, so
    // they aren't mistaken for new presses when the link comes back.
    private void ResetForLinkLoss()
    {
        _plugBackoff = PlugBackoffMin;
        _input = new InputTracker(_config, heldBeforeDrop: _input.KnownHeld & AnyButton);
        _output = null;
        _feedback.Clear();
        _player = null;
        _playerPollAt = null;
        _largeMotor = _smallMotor = 0;
        _rumbleAt = null;
        _outputAt = null;
    }

    // ---- events ----

    private void OnInput(Input input, long now)
    {
        // Chords run on processing time like the rest of the core, so a report stepped late can't cut a
        // Guide tap short; the camera keeps the report's own timestamp for velocity.
        var actions = _input.Apply(input.Report, now);
        _output?.Update(_input.State, input.Timestamp);
        foreach (var action in actions) Run(action, now);
        TryRestoreMouseMode(now);
        RefreshOutput(now);
    }

    private void Run(ChordAction action, long now)
    {
        switch (action)
        {
            case ChordAction.ToggleCapture when _life is Gamepad:
                Unplug();
                _output = null;
                _life = new Releasing(RetryAt: 0);
                _lizardFailures = 0;
                _log(LogLevel.Info, "capture off: controller is back in mouse/keyboard mode");
                Confirm(now, 2, ScPad.Left, ScPad.Right);
                break;
            case ChordAction.ToggleCapture when _life is Releasing or Mouse:
                EnterGamepad(now);
                _log(LogLevel.Info, "capture on");
                Confirm(now, 1, ScPad.Left, ScPad.Right);
                break;
            case ChordAction.CycleRightPadMode when _life is Gamepad:
                // Camera -> Stick -> Camera, passing through None only if that's the configured mode.
                _mode = _mode switch
                {
                    RightPadMode.Camera => RightPadMode.Stick,
                    RightPadMode.Stick when _config.RightPadMode == RightPadMode.None => RightPadMode.None,
                    _ => RightPadMode.Camera,
                };
                // The chord is a click with the thumb on the pad; in Stick mode that position would
                // immediately deflect the stick, so the new stage ignores the pad until the thumb lifts.
                _output = new OutputStage(_config, _mode, muted: _input.State.RPadTouched);
                _log(LogLevel.Info, $"right pad mode: {_mode}");
                Confirm(now, _mode switch { RightPadMode.Camera => 1, RightPadMode.Stick => 2, _ => 3 }, ScPad.Right);
                break;
            // Pad-mode switching while capture is off: there's no stick to switch.
        }
    }

    private void OnTick(long now)
    {
        if (_input.WakeAt is { } wake && wake <= now) _input.Reprocess(now);

        if (_life is Gamepad { PadPlugged: false, PlugRetryAt: var plugAt } && plugAt <= now) Plug(now);

        for (var i = 0; i < _feedback.Count; i++)
        {
            if (_feedback[i].At > now) continue;
            Emit(_feedback[i].Effect);
            _feedback.RemoveAt(i--);
        }

        if (_rumbleAt is { } rumbleAt && rumbleAt <= now)
        {
            _rumbleAt = null;
            if (_life is Gamepad { PadPlugged: true } && (_largeMotor | _smallMotor) != 0)
            {
                EmitRumble();
                _rumbleAt = now + RumbleTick;
            }
        }

        if (_playerPollAt is { } pollAt && pollAt <= now)
        {
            _playerPollAt = null; // wait for the answer
            Emit(new QueryPlayerIndex(_padGeneration));
        }

        TryRestoreMouseMode(now);
        RefreshOutput(now);
    }

    private void OnRumble(Rumble rumble, long now)
    {
        if (!_config.Rumble || _life is not Gamepad { PadPlugged: true } || rumble.PadGeneration != _padGeneration) return;
        _largeMotor = rumble.Large;
        _smallMotor = rumble.Small;
        if ((_largeMotor | _smallMotor) == 0 || _rumbleAt != null) return;
        EmitRumble();
        _rumbleAt = now + RumbleTick;
    }

    private void OnPlayerIndex(PlayerIndexResult result, long now)
    {
        if (_life is not Gamepad { PadPlugged: true } || result.PadGeneration != _padGeneration) return;
        if (result.Index is { } index)
        {
            _player = index;
            _playerPollAt = null;
            _log(LogLevel.Info, $"virtual pad is XInput player {index + 1}");
        }
        else if (--_playerPollsLeft > 0)
        {
            _playerPollAt = now + PlayerPollInterval;
        }
    }

    private void OnFailed(Effect effect, long now)
    {
        switch (effect)
        {
            case SetLizard { Enabled: true } when _life is Mouse:
                // Releasing moved to Mouse optimistically; go back and retry later.
                if (_lizardFailures++ == 0) _log(LogLevel.Warn, "could not restore mouse/keyboard mode, retrying");
                _life = new Releasing(RetryAt: now + LizardRetry);
                break;
            case SetLizard { Enabled: false } when _life is Gamepad:
                _log(LogLevel.Warn, "could not disable lizard mode; trackpad may still move the mouse");
                break;
            case PlugPad plug when _life is Gamepad && plug.Generation == _padGeneration:
                _submitPending = false; // the shell skipped the Submit that followed: there was no pad
                _log(LogLevel.Error, $"could not create the virtual pad; retrying in {_plugBackoff * 1000 / Stopwatch.Frequency} ms");
                _life = new Gamepad(PadPlugged: false, PlugRetryAt: now + _plugBackoff);
                _plugBackoff = Math.Min(_plugBackoff * 2, PlugBackoffMax);
                _output = new OutputStage(_config, _mode, muted: false);
                _playerPollAt = null;
                _outputAt = null;
                break;
            case Submit when _life is Gamepad { PadPlugged: true } && _output != null:
                _output.LastSubmitted = null; // resend on the next output tick
                _outputAt = now + OutputTick;
                if (_submitFailures++ == 0) _log(LogLevel.Warn, "submitting to the virtual pad failed; retrying");
                if (_submitFailures >= SubmitFailuresBeforeReplug)
                {
                    _log(LogLevel.Error, $"virtual pad rejected {_submitFailures} reports in a row; re-creating it");
                    Unplug();
                    _life = new Gamepad(PadPlugged: false, PlugRetryAt: now + _plugBackoff);
                    _plugBackoff = Math.Min(_plugBackoff * 2, PlugBackoffMax);
                }
                break;
            case QueryPlayerIndex query:
                OnPlayerIndex(new PlayerIndexResult(query.Generation, null), now);
                break;
        }
    }

    // ---- helpers ----

    // Enabling mouse mode while a button is held makes the firmware type that button's key (Back is Tab),
    // so wait until every button is up.
    private void TryRestoreMouseMode(long now)
    {
        if (_life is not Releasing { RetryAt: var retryAt } || retryAt > now || !AllButtonsUp) return;
        Emit(new SetLizard(true));
        _life = new Mouse(); // optimistic; EffectFailed moves it back
    }

    private void RefreshOutput(long now)
    {
        _outputAt = null;
        if (_life is not Gamepad { PadPlugged: true } || _output == null) return;
        var x = _output.Build(_input.State, _input.Visible, now);
        if (x != _output.LastSubmitted)
        {
            Emit(new Submit(x));
            _output.LastSubmitted = x;
        }
        if (_output.NeedsTicks(now)) _outputAt = now + OutputTick;
    }

    /// <summary>Confirmation buzz: <paramref name="ticks"/> short pulses, replacing any still pending.</summary>
    private void Confirm(long now, int ticks, params ScPad[] pads)
    {
        _feedback.Clear();
        for (var i = 0; i < ticks; i++)
            foreach (var pad in pads)
                _feedback.Add((now + FeedbackDelay + i * FeedbackSpacing, new Haptic(pad, 2000, 2000, 10))); // 40 ms at 250 Hz
    }

    // XInput rumble is a level that games set and forget, but the Steam Controller only does finite pulse
    // trains, so re-issue short trains every RumbleTick while a motor is on. A pad plays one train at a time:
    // a motor on its own drives both pads (one-sided rumble feels wrong when the game meant the whole
    // controller); with both on, they split like an Xbox controller's, large on the left and small on the right.
    private void EmitRumble()
    {
        (RumbleMotor Motor, byte Level) large = (LargeMotor, _largeMotor), small = (SmallMotor, _smallMotor);
        var (left, right) = (_largeMotor, _smallMotor) switch
        {
            (0, _) => (small, small),
            (_, 0) => (large, large),
            _ => (large, small),
        };
        Drive(ScPad.Left, left.Motor, left.Level);
        Drive(ScPad.Right, right.Motor, right.Level);
    }

    private void Drive(ScPad pad, RumbleMotor motor, byte level)
    {
        var strength = Math.Clamp(level / 255.0 * _config.RumbleStrength, 0, 1);
        if (!(strength > 0)) return; // also NaN
        var on = (ushort)Math.Round(motor.MinOnUs * Math.Pow((double)motor.MaxOnUs / motor.MinOnUs, strength));
        var off = (ushort)(motor.PeriodUs - on);
        var count = (ushort)Math.Ceiling(RumbleTick * 1000.0 / Stopwatch.Frequency * 1000 * 1.5 / motor.PeriodUs); // overlap the next train
        Emit(new Haptic(pad, on, off, count));
    }

    private sealed record RumbleMotor(ushort PeriodUs, ushort MinOnUs, ushort MaxOnUs);
}
