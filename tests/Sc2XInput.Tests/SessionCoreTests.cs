using Sc2XInput.SteamController;
using static Sc2XInput.Tests.Time;
using B = Sc2XInput.SteamController.ScButtons;

namespace Sc2XInput.Tests;

/// <summary>
/// Scenario tests for <see cref="SessionCore"/>. Many are regressions for review findings on PRs #2 and #3;
/// the harness also checks the session invariants after every step.
/// </summary>
public class SessionCoreTests
{
    private static CoreHarness Connected(Config? config = null)
    {
        var h = new CoreHarness(config);
        h.Step(new Connected());
        return h;
    }

    private static List<Effect> EffectsDuring(CoreHarness h, Action act)
    {
        var before = h.Executed.Count;
        act();
        return h.Executed.Skip(before).Select(e => e.Effect).ToList();
    }

    private static void CaptureOff(CoreHarness h, double at)
    {
        h.ReportAt(at, B.Steam);
        h.ReportAt(at + 10, B.Steam | B.Y);
        h.ReportAt(at + 20, B.None);
    }

    // ---- connect, capture, mouse mode ----

    [Fact]
    public void Connect_TurnsLizardOff_PlugsAPad_AndSendsANeutralReport()
    {
        var h = new CoreHarness();
        var effects = EffectsDuring(h, () => h.Step(new Connected()));
        Assert.Equal([new SetLizard(false), new PlugPad(1), new Submit(default)], effects);
        Assert.True(h.PadConnected);
        Assert.False(h.LizardOn);
        Assert.Equal(new SessionStatus(true, true, false, null, RightPadMode.Camera), h.Core.Status);
    }

    [Fact]
    public void FirstInput_WithoutAConnectionEvent_AlsoConnects_AndIsApplied()
    {
        var h = new CoreHarness();
        h.Report(B.A);
        Assert.True(h.PadConnected);
        Assert.Equal(XButtons.A, h.LastSubmitted?.Buttons);
    }

    [Fact]
    public void DuplicateConnected_IsIgnored()
    {
        var h = Connected();
        Assert.Empty(EffectsDuring(h, () => h.Step(new Connected())));
    }

    [Fact]
    public void CaptureOff_UnplugsAndRestoresMouseMode_OnlyOnceAllButtonsAreUp()
    {
        var h = Connected();
        h.ReportAt(0, B.Steam);
        var toggle = EffectsDuring(h, () => h.ReportAt(10, B.Steam | B.Y));
        Assert.Contains(new UnplugPad(), toggle);
        Assert.DoesNotContain(new SetLizard(true), toggle); // Steam and Y still held: would type their keys
        Assert.False(h.LizardOn);

        h.ReportAt(20, B.Y);
        Assert.False(h.LizardOn);
        h.ReportAt(30, B.None);
        Assert.True(h.LizardOn);
        Assert.IsType<SessionCore.Mouse>(h.Core.State);
        Assert.Equal(new SessionStatus(true, false, false, null, RightPadMode.Camera), h.Core.Status);
    }

    [Fact]
    public void HoldingSteamY_AfterRecapture_TogglesExactlyOnce_AndNeverSendsYOrGuide()
    {
        // The plan's first draft rebuilt chord tracking on recapture, which would read the still-held Y as a
        // new chord and toggle straight back off.
        var h = new CoreHarness(captured: false);
        h.Step(new Connected());
        h.ReportAt(0, B.Steam);
        h.ReportAt(10, B.Steam | B.Y);
        for (var t = 20; t <= 600; t += 10) h.ReportAt(t, B.Steam | B.Y);
        h.ReportAt(610, B.Y);
        h.ReportAt(620, B.None);
        h.AdvanceTo(1000);

        Assert.Equal(1, h.Count<PlugPad>());
        Assert.Equal(0, h.Count<UnplugPad>());
        Assert.All(h.Submits, s => Assert.Equal(XButtons.None, s.State.Buttons & (XButtons.Y | XButtons.Guide)));
    }

    [Fact]
    public void Recapture_TurnsLizardOff_AndSubmitsThatReportsMapping()
    {
        var h = new CoreHarness(captured: false);
        h.Step(new Connected());
        h.ReportAt(0, B.A);
        h.ReportAt(10, B.A | B.Steam);
        var effects = EffectsDuring(h, () => h.ReportAt(20, B.A | B.Steam | B.Y));
        Assert.Equal(new SetLizard(false), effects[0]);
        Assert.IsType<PlugPad>(effects[1]);
        Assert.Equal(XButtons.A, h.LastSubmitted?.Buttons); // Y is the chord button: hidden
    }

    [Fact]
    public void StartingUncaptured_WakesIntoMouseMode_WithoutPlugging()
    {
        var h = new CoreHarness(captured: false);
        h.Step(new Connected());
        h.ReportAt(10, B.A);
        Assert.IsType<SessionCore.Mouse>(h.Core.State);
        Assert.Equal(0, h.Count<PlugPad>());
        Assert.Empty(h.Submits);
    }

    [Fact]
    public void PadModeChord_WhileCaptureIsOff_DoesNothing()
    {
        var h = new CoreHarness(captured: false);
        h.Step(new Connected());
        var effects = EffectsDuring(h, () =>
        {
            h.ReportAt(0, B.Steam);
            h.ReportAt(10, B.Steam | B.RPadClick);
            h.ReportAt(20, B.None);
            h.AdvanceTo(1000);
        });
        Assert.Empty(effects); // no mode change, no buzz
        Assert.Equal(RightPadMode.Camera, h.Core.Status.Mode);
    }

    // ---- link loss ----

    [Fact]
    public void ChordHeldThroughAReconnect_DoesNotRunAgain()
    {
        // Found by the randomized test: the new link's chord tracking saw the still-held Steam+Y as a
        // fresh chord and toggled capture back on.
        var h = Connected();
        h.ReportAt(0, B.Steam);
        h.ReportAt(10, B.Steam | B.Y); // capture off
        h.Step(new Disconnected());
        h.AdvanceTo(500);
        h.Step(new Connected());
        h.ReportAt(510, B.Steam | B.Y); // still held
        h.ReportAt(520, B.None);
        h.AdvanceTo(1000);
        Assert.Single(h.Messages, m => m.Text.StartsWith("capture o"));
        Assert.Equal(1, h.Count<PlugPad>());
        Assert.False(h.Core.Status.Captured);
    }

    [Fact]
    public void FirstReportAfterAReconnect_CanStillBeARealSteamPress()
    {
        var h = Connected();
        h.Step(new Disconnected());
        h.AdvanceTo(500);
        h.Step(new Connected());
        h.ReportAt(510, B.Steam); // the dongle only reports changes, so this can be the first report
        h.ReportAt(560, B.None);
        Assert.Equal(XButtons.Guide, h.LastSubmitted?.Buttons);
    }

    [Fact]
    public void DisconnectWhileSteamYHeld_ThenTimePasses_RunsNoChord_AndReconnectStartsClean()
    {
        var h = Connected();
        h.ReportAt(0, B.Steam);
        h.AdvanceTo(100);
        h.Step(new Disconnected());
        var asleep = EffectsDuring(h, () => h.AdvanceTo(3000));
        Assert.Empty(asleep); // the chord timer doesn't fire on stale buttons while it sleeps
        Assert.Equal(new SessionStatus(false, true, false, null, RightPadMode.Camera), h.Core.Status);

        h.Step(new Connected());
        h.ReportAt(3010, B.Steam); // a fresh press: no instant Guide from the press before the drop
        Assert.Equal(XButtons.None, h.LastSubmitted?.Buttons);
    }

    [Fact]
    public void Reconnect_DoesNotReplayInputFromBeforeTheDrop()
    {
        var h = Connected();
        h.Step(new Input(Reports.Make(B.A, lt: 200, x: 20000), h.Now));
        Assert.NotEqual(default, h.LastSubmitted);
        h.Step(new Disconnected());
        h.AdvanceTo(1000);
        var reconnect = EffectsDuring(h, () => h.Step(new Connected()));
        Assert.Equal(new Submit(default), reconnect.OfType<Submit>().Single());
    }

    [Fact]
    public void DropMidRestore_FinishesRestoringMouseModeOnReconnect()
    {
        var h = Connected();
        h.ReportAt(0, B.Steam);
        h.ReportAt(10, B.Steam | B.Y); // capture off, restore pending while buttons are held
        h.Step(new Disconnected());
        Assert.False(h.LizardOn);
        h.AdvanceTo(1000);
        h.Step(new Connected());
        h.AdvanceTo(1500);
        Assert.False(h.LizardOn); // which buttons are held is unknown until the first report
        h.ReportAt(1510, B.A);
        Assert.False(h.LizardOn); // a button is held: enabling now would type its key
        h.ReportAt(1520, B.None);
        Assert.True(h.LizardOn);
        Assert.IsType<SessionCore.Mouse>(h.Core.State);
    }

    [Fact]
    public void DeviceLost_UnplugsWithoutLizardWrites_AndCloses()
    {
        var h = Connected();
        var effects = EffectsDuring(h, () => h.Step(new DeviceLost()));
        Assert.Equal([new UnplugPad()], effects);
        Assert.True(h.Core.Status.IsClosed);
        Assert.Empty(EffectsDuring(h, () => { h.Report(B.A); h.Step(new Connected()); h.AdvanceTo(5000); }));
    }

    // ---- close ----

    [Fact]
    public void Close_FromGamepad_UnplugsAndRestoresMouseMode()
    {
        var h = Connected();
        Assert.Equal([new UnplugPad(), new SetLizard(true)], EffectsDuring(h, () => h.Step(new Close())));
        Assert.True(h.Core.Status.IsClosed);
    }

    [Fact]
    public void Close_WhileRestoreIsPendingWithButtonsHeld_StillRestores()
    {
        var h = Connected();
        h.ReportAt(0, B.Steam);
        h.ReportAt(10, B.Steam | B.Y);
        Assert.Equal([new SetLizard(true)], EffectsDuring(h, () => h.Step(new Close())));
    }

    [Fact]
    public void Close_FromMouseMode_DoesNothing()
    {
        var h = Connected();
        CaptureOff(h, 0);
        Assert.Empty(EffectsDuring(h, () => h.Step(new Close())));
    }

    [Fact]
    public void Close_WhileAsleepWithARestorePending_Restores()
    {
        var h = Connected();
        h.ReportAt(0, B.Steam);
        h.ReportAt(10, B.Steam | B.Y);
        h.Step(new Disconnected());
        Assert.Equal([new SetLizard(true)], EffectsDuring(h, () => h.Step(new Close())));
    }

    // ---- failures ----

    [Fact]
    public void FailedMouseModeRestore_RetriesEvery500ms_AndLogsOnce()
    {
        var h = Connected();
        var failures = 2;
        h.Fail = e => e is SetLizard { Enabled: true } && failures-- > 0;
        CaptureOff(h, 0); // restore attempted at 20 ms, fails
        Assert.IsType<SessionCore.Releasing>(h.Core.State);
        Assert.DoesNotContain(EffectsDuring(h, () => h.AdvanceTo(519)), e => e is SetLizard);
        h.AdvanceTo(520);  // second attempt, fails
        h.AdvanceTo(1020); // third succeeds
        Assert.True(h.LizardOn);
        Assert.Equal(3, h.Effects.Count(e => e is SetLizard { Enabled: true }));
        Assert.Single(h.Messages, m => m.Level == LogLevel.Warn);
    }

    [Fact]
    public void FailedLizardOff_WarnsAndStillPlugs()
    {
        var h = new CoreHarness();
        var once = true;
        h.Fail = e => e is SetLizard { Enabled: false } && (once, once = false).once;
        h.Step(new Connected());
        Assert.True(h.PadConnected);
        Assert.Contains(h.Messages, m => m.Level == LogLevel.Warn && m.Text.Contains("lizard"));
    }

    [Fact]
    public void FailedPlug_RetriesWithBackoff()
    {
        var h = new CoreHarness();
        var failures = 2;
        h.Fail = e => e is PlugPad && failures-- > 0;
        h.Step(new Connected()); // fails; retry at 500 ms
        Assert.False(h.PadConnected);
        h.AdvanceTo(499);
        Assert.Equal(1, h.Count<PlugPad>());
        h.AdvanceTo(500); // fails; retry 1 s later
        h.AdvanceTo(1499);
        Assert.False(h.PadConnected);
        h.AdvanceTo(1500);
        Assert.True(h.PadConnected);
        Assert.NotNull(h.LastSubmitted);
        Assert.Equal(2, h.Messages.Count(m => m.Level == LogLevel.Error));
    }

    [Fact]
    public void FailedSubmits_AreRetried_ThenThePadIsRecreated()
    {
        var h = new CoreHarness();
        var failing = true;
        h.Fail = e => failing && e is Submit;
        h.Step(new Connected());
        h.AdvanceTo(300);
        Assert.True(h.Count<Submit>() >= 50);
        Assert.Equal(1, h.Count<UnplugPad>());
        Assert.Contains(h.Messages, m => m.Level == LogLevel.Error && m.Text.Contains("re-creating"));

        failing = false;
        h.AdvanceTo(2000);
        Assert.True(h.PadConnected);
        Assert.NotNull(h.LastSubmitted);
    }

    [Fact]
    public void SubmitRecovery_IsLogged()
    {
        var h = new CoreHarness();
        var failures = 3;
        h.Fail = e => e is Submit && failures-- > 0;
        h.Step(new Connected());
        h.AdvanceTo(100);
        Assert.NotNull(h.LastSubmitted);
        Assert.Single(h.Messages, m => m.Level == LogLevel.Warn);
        Assert.Contains(h.Messages, m => m.Text.Contains("after 3 failures"));
    }

    // ---- rumble and player index ----

    [Fact]
    public void Rumble_IsReissuedEvery50ms_WhileOn()
    {
        var h = Connected();
        var gen = h.Generation;
        var start = EffectsDuring(h, () => h.Step(new Rumble(gen, 255, 0)));
        Assert.Equal(ScPad.Left, Assert.IsType<Haptic>(Assert.Single(start)).Pad);
        Assert.Equal(2, EffectsDuring(h, () => h.Advance(100)).OfType<Haptic>().Count());
        h.Step(new Rumble(gen, 0, 0));
        Assert.Empty(EffectsDuring(h, () => h.Advance(200)).OfType<Haptic>());
    }

    [Fact]
    public void Rumble_IsIgnored_ForAStalePad_WhenDisabled_OrOutsideGamepad()
    {
        var h = Connected();
        Assert.Empty(EffectsDuring(h, () => h.Step(new Rumble(h.Generation - 1, 255, 255))));

        var off = Connected(new Config { Rumble = false });
        Assert.Empty(EffectsDuring(off, () => off.Step(new Rumble(off.Generation, 255, 255))));

        var gen = h.Generation;
        CaptureOff(h, 0);
        var afterCaptureOff = EffectsDuring(h, () => { h.Step(new Rumble(gen, 255, 255)); h.Advance(200); }).OfType<Haptic>();
        Assert.DoesNotContain(afterCaptureOff, x => x.Count != 10); // only the capture-off confirmation ticks
    }

    [Fact]
    public void PlayerIndex_IsPolled_UntilAssigned()
    {
        var h = Connected();
        var gen = h.Generation;
        Assert.Equal([new QueryPlayerIndex(gen)], EffectsDuring(h, () => h.AdvanceTo(100)));
        h.Step(new PlayerIndexResult(gen, null));
        Assert.Equal([new QueryPlayerIndex(gen)], EffectsDuring(h, () => h.AdvanceTo(200)));
        h.Step(new PlayerIndexResult(gen - 1, 3)); // stale pad
        Assert.Null(h.Core.Status.Player);
        h.Step(new PlayerIndexResult(gen, 0));
        Assert.Equal(0, h.Core.Status.Player);
        Assert.Null(h.Core.NextDeadline); // idle
    }

    [Fact]
    public void PlayerIndex_IsClearedWhenThePadIsUnplugged()
    {
        var h = Connected();
        h.Step(new PlayerIndexResult(h.Generation, 1));
        CaptureOff(h, 0);
        Assert.Null(h.Core.Status.Player);
    }

    // ---- right pad modes ----

    [Fact]
    public void PadModeChord_CyclesCameraStick_AndIgnoresThePadUntilTheThumbLifts()
    {
        var h = Connected();
        h.ReportAt(0, B.Steam);
        h.ReportAt(10, B.Steam | B.RPadClick, rpad: true, rx: 5000);
        Assert.Equal(RightPadMode.Stick, h.Core.Status.Mode);
        h.ReportAt(20, B.None, rpad: true, rx: 30000);
        Assert.Equal((short)0, h.LastSubmitted?.RightX); // muted: the thumb that clicked is still down
        h.ReportAt(30, B.None);
        h.ReportAt(40, B.None, rpad: true, rx: 30000);
        Assert.Equal(short.MaxValue, h.LastSubmitted?.RightX);

        h.ReportAt(50, B.Steam);
        h.ReportAt(60, B.Steam | B.RPadClick);
        Assert.Equal(RightPadMode.Camera, h.Core.Status.Mode);
    }

    [Fact]
    public void PadModeCycle_IncludesNone_OnlyWhenConfigured()
    {
        var h = Connected(new Config { RightPadMode = RightPadMode.None });
        var modes = new List<RightPadMode>();
        for (var i = 0; i < 3; i++)
        {
            h.ReportAt(i * 100, B.Steam);
            h.ReportAt(i * 100 + 10, B.Steam | B.RPadClick);
            h.ReportAt(i * 100 + 20, B.None);
            modes.Add(h.Core.Status.Mode);
        }
        Assert.Equal([RightPadMode.Camera, RightPadMode.Stick, RightPadMode.None], modes);
    }

    [Fact]
    public void ModeSwitchHaptics_OneTwoThreeTicks_OnTheRightPad()
    {
        var h = Connected(new Config { RightPadMode = RightPadMode.None });
        int Ticks(double at)
        {
            h.ReportAt(at, B.Steam);
            h.ReportAt(at + 10, B.Steam | B.RPadClick);
            h.ReportAt(at + 20, B.None);
            var effects = EffectsDuring(h, () => h.AdvanceTo(at + 1000));
            Assert.All(effects.OfType<Haptic>(), x => Assert.Equal(ScPad.Right, x.Pad));
            return effects.OfType<Haptic>().Count();
        }
        Assert.Equal([1, 2, 3], new[] { Ticks(0), Ticks(2000), Ticks(4000) });
    }

    [Fact]
    public void MutedTouch_WithTrackball_DoesNotCoastAfterTheLift()
    {
        var h = Connected(new Config { RightPadMode = RightPadMode.Stick, CameraTrackball = true });
        h.ReportAt(0, B.Steam);
        h.ReportAt(10, B.Steam | B.RPadClick, rpad: true); // -> Camera, muted while touched
        for (var i = 0; i < 10; i++) h.ReportAt(20 + i * 10, B.None, rpad: true, rx: (short)(i * 3000)); // fast flick
        h.ReportAt(120, B.None);
        h.AdvanceTo(600);
        Assert.All(h.Submits, s => Assert.Equal(0, s.State.RightX));
    }

    [Fact]
    public void MotionInStickMode_DoesNotLeakIntoCameraMode()
    {
        var h = Connected(new Config { RightPadMode = RightPadMode.Stick, CameraTrackball = true });
        for (var i = 0; i < 10; i++) h.ReportAt(i * 10, B.None, rpad: true, rx: (short)(i * 3000));
        h.ReportAt(100, B.None);
        h.ReportAt(200, B.Steam);
        h.ReportAt(210, B.Steam | B.RPadClick); // -> Camera, thumb not on the pad
        h.ReportAt(220, B.None);
        var after = h.Executed.Count;
        h.AdvanceTo(800);
        Assert.All(h.Executed.Skip(after).Select(e => e.Effect).OfType<Submit>(), s => Assert.Equal(0, s.State.RightX));
        Assert.Equal((short)0, h.LastSubmitted?.RightX);
    }

    [Fact]
    public void Camera_WhenReportsStall_FadesToZero_ThenGoesIdle()
    {
        var h = Connected();
        h.Step(new PlayerIndexResult(h.Generation, 0));
        for (var i = 0; i <= 20; i++) h.ReportAt(i * 10, B.None, rpad: true, rx: (short)(i * 655)); // 2 radii/s
        Assert.True(h.LastSubmitted?.RightX > 0);
        h.AdvanceTo(400); // no more reports
        Assert.Equal((short)0, h.LastSubmitted?.RightX);
        Assert.Null(h.Core.NextDeadline);
    }

    // ---- Guide and feedback ----

    [Fact]
    public void GuideTap_AndHold_ReachTheGame()
    {
        var h = Connected();
        h.ReportAt(0, B.Steam);
        Assert.Equal(XButtons.None, h.LastSubmitted?.Buttons);
        h.ReportAt(100, B.None);
        Assert.Equal(XButtons.Guide, h.LastSubmitted?.Buttons);
        h.AdvanceTo(250);
        Assert.Equal(XButtons.None, h.LastSubmitted?.Buttons);

        h.ReportAt(1000, B.Steam);
        h.AdvanceTo(1400); // held long enough: Guide held via the chord wake-up, without new input
        Assert.Equal(XButtons.Guide, h.LastSubmitted?.Buttons);
        h.ReportAt(2000, B.None);
        Assert.Equal(XButtons.None, h.LastSubmitted?.Buttons);
    }

    [Fact]
    public void CaptureOffFeedback_TwoTicksOnBothPads_AfterTheDelay()
    {
        var h = Connected();
        h.ReportAt(0, B.Steam);
        h.ReportAt(10, B.Steam | B.Y);
        h.ReportAt(20, B.None);
        Assert.Empty(EffectsDuring(h, () => h.AdvanceTo(259)).OfType<Haptic>());
        Assert.Equal(2, EffectsDuring(h, () => h.AdvanceTo(260)).OfType<Haptic>().Count());
        Assert.Equal(2, EffectsDuring(h, () => h.AdvanceTo(460)).OfType<Haptic>().Count());
    }

    [Fact]
    public void PendingFeedback_IsDropped_WhenTheLinkDrops()
    {
        var h = Connected();
        h.ReportAt(0, B.Steam);
        h.ReportAt(10, B.Steam | B.Y);
        h.Step(new Disconnected());
        Assert.Empty(EffectsDuring(h, () => h.AdvanceTo(2000)));
    }

    [Fact]
    public void Idle_HasNoDeadline_InEveryState()
    {
        var h = Connected();
        h.Step(new PlayerIndexResult(h.Generation, 0));
        h.ReportAt(10, B.A);
        h.ReportAt(20, B.None);
        h.AdvanceTo(100);
        Assert.Null(h.Core.NextDeadline); // idle gamepad

        CaptureOff(h, 200);
        h.AdvanceTo(2000); // feedback done
        Assert.Null(h.Core.NextDeadline); // idle mouse mode
    }
}
