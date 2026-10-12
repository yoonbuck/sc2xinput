using Sc2XInput.SteamController;
using static Sc2XInput.Tests.Time;
using B = Sc2XInput.SteamController.ScButtons;

namespace Sc2XInput.Tests;

public class SteamChordsTests
{
    private static readonly Dictionary<ScButtons, ChordAction> Bindings = new()
    {
        [B.Back] = ChordAction.ToggleCapture,
        [B.RPadClick] = ChordAction.CycleRightPadMode,
    };

    /// <summary>One <see cref="SteamChords.Process"/> call and what it should return.</summary>
    /// <param name="Visible">Buttons the game should see, not counting Guide.</param>
    /// <param name="WakeMs">Expected <c>WakeAt</c>, or null to not check it.</param>
    private sealed record Step(double Ms, B Raw, bool Guide, B Visible = B.None, ChordAction[]? Actions = null, double? WakeMs = null);

    private static void Run(params Step[] steps) => RunWith(new SteamChords(Bindings), steps);

    private static void RunWith(SteamChords chords, params Step[] steps)
    {
        foreach (var step in steps)
        {
            var r = chords.Process(step.Raw, Ms(step.Ms));
            var at = $"at {step.Ms} ms";
            Assert.True(step.Guide == ((r.Buttons & B.Steam) != 0), $"Guide {at}");
            Assert.True(step.Visible == (r.Buttons & ~B.Steam), $"visible buttons {at}: {r.Buttons & ~B.Steam}");
            Assert.True((step.Actions ?? []).SequenceEqual(r.Actions), $"actions {at}: [{string.Join(",", r.Actions)}]");
            if (step.WakeMs is { } wake)
                Assert.True(r.WakeAt is { } w && Math.Abs(ToMs(w) - wake) < 0.01, $"wake {at}: {(r.WakeAt is { } x ? ToMs(x) : null)}");
        }
    }

    [Fact]
    public void Tap_SendsShortGuideTapOnRelease() => Run(
        new(0, B.Steam, false, WakeMs: 400),
        new(120, B.None, true, WakeMs: 220),
        new(221, B.None, false));

    [Fact]
    public void Hold_HoldsGuideFrom400msUntilRelease() => Run(
        new(0, B.Steam, false, WakeMs: 400),
        new(400, B.Steam, true),
        new(2000, B.Steam, true),
        new(2500, B.None, false));

    [Fact]
    public void Chord_RunsActionWithoutGuideAndHidesTheButton() => Run(
        new(0, B.Steam, false),
        new(100, B.Steam | B.Back, false, Actions: [ChordAction.ToggleCapture]),
        new(200, B.Steam | B.Back, false),
        new(300, B.Back, false),
        new(400, B.None, false),
        new(600, B.None, false));

    [Fact]
    public void ChordButton_StaysHiddenUntilReleased_EvenAfterSteam() => Run(
        new(0, B.Steam, false),
        new(100, B.Steam | B.Back, false, Actions: [ChordAction.ToggleCapture]),
        new(150, B.Back, false),
        new(300, B.Back | B.A, false, B.A),
        new(400, B.A, false, B.A));

    [Fact]
    public void UnboundButton_PassesThrough_AndSteamStillTaps() => Run(
        new(0, B.Steam, false),
        new(50, B.Steam | B.A, false, B.A),
        new(100, B.Steam, false),
        new(150, B.None, true));

    [Fact]
    public void TwoChords_InOneHold() => Run(
        new(0, B.Steam, false),
        new(100, B.Steam | B.RPadClick, false, Actions: [ChordAction.CycleRightPadMode]),
        new(200, B.Steam, false),
        new(300, B.Steam | B.RPadClick, false, Actions: [ChordAction.CycleRightPadMode]),
        new(400, B.None, false));

    [Fact]
    public void BoundButton_WithoutSteam_IsNormal() => Run(
        new(0, B.Back, false, B.Back),
        new(100, B.None, false));

    [Fact]
    public void ButtonHeldBeforeSteam_IsNotAChord_SteamTaps() => Run(
        new(0, B.Back, false, B.Back),
        new(100, B.Back | B.Steam, false, B.Back, WakeMs: 500),
        new(200, B.Back, true, B.Back));

    [Fact]
    public void ReprocessingUnchangedInput_DoesNotRefire() => Run(
        new(0, B.Steam, false),
        new(100, B.Steam | B.Back, false, Actions: [ChordAction.ToggleCapture]),
        new(101, B.Steam | B.Back, false),
        new(500, B.Steam | B.Back, false));

    [Fact]
    public void ChordAfterGuideHoldStarted_IsIgnored() => Run(
        new(0, B.Steam, false),
        new(400, B.Steam, true),
        new(500, B.Steam | B.Back, true, B.Back),
        new(600, B.None, false));

    [Fact]
    public void ChordRightAfterTap_EndsTheTap_NoGuideDuringChord() => Run(
        new(0, B.Steam, false),
        new(50, B.None, true, WakeMs: 150),
        new(80, B.Steam, false, WakeMs: 480),
        new(90, B.Steam | B.Back, false, Actions: [ChordAction.ToggleCapture]),
        new(200, B.None, false));

    [Fact]
    public void DoubleTap_IsTwoSeparateGuidePresses() => Run(
        new(0, B.Steam, false),
        new(50, B.None, true),
        new(80, B.Steam, false),
        new(110, B.None, true, WakeMs: 210),
        new(211, B.None, false));

    [Fact]
    public void LateHoldTimer_ChordReportPast400ms_IsGuide_NotAction() => Run(
        new(0, B.Steam, false, WakeMs: 400),
        new(450, B.Steam | B.Back, true, B.Back),
        new(500, B.None, false));

    [Fact]
    public void LateHoldTimer_ReleaseBeforeTimerFires_StillTaps() => Run(
        new(0, B.Steam, false, WakeMs: 400),
        new(450, B.None, true, WakeMs: 550),
        new(551, B.None, false));

    [Fact]
    public void Reset_ForgetsSteamHeldAcrossDisconnect()
    {
        var chords = new SteamChords(Bindings);
        chords.Process(B.Steam, Ms(0));
        chords.Reset();
        RunWith(chords, new Step(5000, B.Steam, false));
    }

    [Fact]
    public void Reset_CancelsAGuideTapInProgress()
    {
        var chords = new SteamChords(Bindings);
        chords.Process(B.Steam, Ms(0));
        chords.Process(B.None, Ms(50)); // tap runs until 150 ms
        chords.Reset();
        RunWith(chords, new Step(70, B.None, false));
    }

    [Fact]
    public void Reset_ReleasesLatchedChordButtons()
    {
        var chords = new SteamChords(Bindings);
        RunWith(chords,
            new Step(0, B.Steam, false),
            new Step(100, B.Steam | B.Back, false, Actions: [ChordAction.ToggleCapture]));
        chords.Reset();
        // Back still held after a reconnect is a plain Back press, not a hidden chord button.
        RunWith(chords, new Step(200, B.Back, false, B.Back));
    }

    [Fact]
    public void Prime_TreatsHeldButtonsAsHeld_NotPressed()
    {
        var chords = new SteamChords(Bindings);
        chords.Prime(B.Steam | B.Back);
        RunWith(chords,
            new Step(0, B.Steam | B.Back, false, B.Back), // no chord, and no Guide hold from a press we didn't see start
            new Step(1000, B.Steam | B.Back, false, B.Back),
            new Step(1100, B.None, false),                // no tap either
            new Step(1300, B.Steam, false),               // a real press afterwards works normally
            new Step(1310, B.Steam | B.Back, false, Actions: [ChordAction.ToggleCapture]));
    }

    [Fact]
    public void NullBindings_DisableChords_SteamIsImmediateGuide() =>
        RunWith(new SteamChords(null), new Step(0, B.Steam, true));

    [Fact]
    public void EmptyBindings_DisableChords_SteamIsImmediateGuide() =>
        RunWith(new SteamChords(new Dictionary<ScButtons, ChordAction>()), new Step(0, B.Steam, true));
}
