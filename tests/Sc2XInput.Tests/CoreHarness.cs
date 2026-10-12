using System.Diagnostics;
using Sc2XInput.SteamController;
using static Sc2XInput.Tests.Time;
using B = Sc2XInput.SteamController.ScButtons;

namespace Sc2XInput.Tests;

/// <summary>
/// Drives a <see cref="SessionCore"/> the way the shell will: synthetic clock, effects executed in order,
/// the whole batch runs and each failure is stepped back afterwards, in order. Keeps a model of the
/// virtual pad and lizard mode, and checks the session invariants after every step.
/// </summary>
internal sealed class CoreHarness
{
    public SessionCore Core { get; }
    public Config Config { get; }
    public long Now { get; private set; }

    /// <summary>Executed effects, in order, with when they ran.</summary>
    public List<(long At, Effect Effect)> Executed { get; } = [];
    public List<(LogLevel Level, string Text)> Messages { get; } = [];

    /// <summary>Return true to make the shell report this effect as failed.</summary>
    public Func<Effect, bool> Fail { get; set; } = _ => false;

    // Shell-side model.
    public bool PadConnected { get; private set; }
    public bool LizardOn { get; private set; } = true; // the firmware's default
    public XState? LastSubmitted { get; private set; }
    private bool _lizardOffAttempted;
    private ScButtons _raw;
    private bool _closing;
    private bool _plugFailedInBatch;

    public CoreHarness(Config? config = null, bool captured = true)
    {
        Config = config ?? new Config();
        Core = new SessionCore(Config, captured, (level, text) => Messages.Add((level, text)));
    }

    public IEnumerable<Effect> Effects => Executed.Select(e => e.Effect);
    public IEnumerable<Submit> Submits => Effects.OfType<Submit>();
    public int Count<T>() where T : Effect => Effects.OfType<T>().Count();

    public void Step(SessionEvent e)
    {
        if (e is Disconnected or DeviceLost) _raw = ScButtons.None; // the shell knows nothing about a dropped link
        if (e is Input i) _raw = (ScButtons)(i.Report[8] | (i.Report[9] << 8) | (i.Report[10] << 16));
        _closing = e is Close;
        var closedBefore = Core.Status.IsClosed;
        var effects = Core.Step(e, Now);

        if (closedBefore) Assert.Empty(effects); // nothing after Close/DeviceLost
        // The whole batch runs; failures are stepped back afterwards, in order, ahead of anything else.
        _plugFailedInBatch = false;
        var failures = effects.Where(effect => !Execute(effect)).ToList();
        foreach (var effect in failures) Step(new EffectFailed(effect));

        if (e is Tick) Assert.True(Core.NextDeadline is not { } d || d > Now, "deadline not in the future after a tick");
        if (Core.State is SessionCore.Asleep or SessionCore.Closed) Assert.Null(Core.NextDeadline);
    }

    private bool Execute(Effect effect)
    {
        var failed = Fail(effect) && effect is not (UnplugPad or Haptic);
        Executed.Add((Now, effect));
        switch (effect)
        {
            case PlugPad:
                Assert.False(PadConnected, "plugged a second pad without unplugging the first");
                Assert.True(_lizardOffAttempted, "plugged a pad without turning lizard mode off first");
                if (!failed) PadConnected = true;
                else _plugFailedInBatch = true;
                break;
            case UnplugPad:
                PadConnected = false;
                LastSubmitted = null;
                break;
            case Submit when !PadConnected && _plugFailedInBatch:
                return true; // the shell skips it: the plug it was for failed (not reported as a failure)
            case Submit s:
                Assert.True(PadConnected, "submitted with no pad plugged in");
                if (!failed) LastSubmitted = s.State;
                break;
            case SetLizard { Enabled: true }:
                Assert.True(_closing || (_raw & AnyButton) == 0, "lizard mode enabled while a button is held");
                if (!failed) { LizardOn = true; _lizardOffAttempted = false; }
                break;
            case SetLizard { Enabled: false }:
                _lizardOffAttempted = true;
                if (!failed) LizardOn = false;
                break;
        }
        return !failed;
    }

    private const ScButtons AnyButton = (ScButtons)0x7FFFFF & ~(ScButtons.LPadTouch | ScButtons.RPadTouch);

    /// <summary>Advances the clock, ticking at every deadline on the way.</summary>
    public void AdvanceTo(double ms)
    {
        var target = Ms(ms);
        while (Core.NextDeadline is { } d && d <= target)
        {
            Now = Math.Max(Now, d);
            Step(new Tick());
        }
        Now = Math.Max(Now, target);
    }

    public void Advance(double ms) => AdvanceTo(ToMs(Now) + ms);

    /// <summary>Sends an input report at the current time.</summary>
    public void Report(ScButtons buttons = B.None, bool rpad = false, short rx = 0, short ry = 0,
        short x = 0, short y = 0, byte lt = 0, byte rt = 0) =>
        Step(new Input(Reports.Make(buttons | (rpad ? B.RPadTouch : 0), lt, rt, x, y, rx, ry), Now));

    /// <summary>Reports <paramref name="buttons"/> at <paramref name="ms"/> (advancing to it first).</summary>
    public void ReportAt(double ms, ScButtons buttons = B.None, bool rpad = false, short rx = 0, short ry = 0)
    {
        AdvanceTo(ms);
        Report(buttons, rpad, rx, ry);
    }

    public int Generation => Effects.OfType<PlugPad>().LastOrDefault()?.Generation ?? 0;
}

internal static class Reports
{
    /// <summary>A 64-byte input report payload (report-ID byte already stripped).</summary>
    public static byte[] Make(ScButtons buttons, byte lt = 0, byte rt = 0, short x = 0, short y = 0, short rx = 0, short ry = 0)
    {
        var d = new byte[64];
        var b = (uint)buttons;
        d[2] = ScProtocol.EvInput;
        d[8] = (byte)b; d[9] = (byte)(b >> 8); d[10] = (byte)(b >> 16);
        d[11] = lt; d[12] = rt;
        BitConverter.TryWriteBytes(d.AsSpan(16), x);
        BitConverter.TryWriteBytes(d.AsSpan(18), y);
        BitConverter.TryWriteBytes(d.AsSpan(20), rx);
        BitConverter.TryWriteBytes(d.AsSpan(22), ry);
        return d;
    }
}
