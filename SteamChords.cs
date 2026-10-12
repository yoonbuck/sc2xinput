using Sc2XInput.SteamController;

namespace Sc2XInput;

internal enum ChordAction
{
    None,
    /// <summary>Unplug the virtual pad and hand the controller back to its mouse/keyboard mode, or the reverse.</summary>
    ToggleCapture,
    /// <summary>Switch the right pad between Camera and Stick mode.</summary>
    CycleRightPadMode,
}

/// <summary>
/// Uses the Steam button as a modifier: Steam + a bound button runs an action instead of pressing either
/// button. Because of that, a plain Steam press can't be forwarded as Guide right away; it is sent as a
/// short tap on release, or held once the button has been down long enough that it can't be a chord.
/// </summary>
internal sealed class SteamChords
{
    private static readonly long HoldTicks = System.Diagnostics.Stopwatch.Frequency * 400 / 1000;
    private static readonly long TapTicks = System.Diagnostics.Stopwatch.Frequency * 100 / 1000;

    private readonly IReadOnlyDictionary<ScButtons, ChordAction> _chords;
    private ScButtons _previous;
    private ScButtons _latched; // chord buttons, hidden from the game until released
    private bool _steamHeld, _chordUsed, _guideHeld;
    private long _steamDownAt, _tapUntil;

    /// <param name="Buttons">Buttons the game should see; <see cref="ScButtons.Steam"/> means "press Guide".</param>
    /// <param name="WakeAt">Timestamp at which to call <see cref="Process"/> again even without new input.</param>
    public readonly record struct Result(ScButtons Buttons, ChordAction[] Actions, long? WakeAt);

    /// <param name="chords">Bindings; null or empty disables chords, so Steam presses Guide immediately.</param>
    public SteamChords(IReadOnlyDictionary<ScButtons, ChordAction>? chords) =>
        _chords = (chords ?? new Dictionary<ScButtons, ChordAction>())
            .Where(c => c.Value != ChordAction.None).ToDictionary();

    /// <summary>Forget held buttons, e.g. when the controller disconnects mid-press.</summary>
    public void Reset()
    {
        _previous = _latched = ScButtons.None;
        _steamHeld = _chordUsed = _guideHeld = false;
        _tapUntil = 0;
    }

    /// <summary>Call with the raw buttons on every report, and at <see cref="Result.WakeAt"/>. Idempotent for unchanged input.</summary>
    public Result Process(ScButtons raw, long now)
    {
        if (_chords.Count == 0) return new(raw, [], null);

        var pressed = raw & ~_previous;
        _previous = raw;
        var steam = (raw & ScButtons.Steam) != 0;
        var actions = new List<ChordAction>();

        if (steam && !_steamHeld)
        {
            _steamHeld = true;
            _chordUsed = _guideHeld = false;
            _steamDownAt = now;
            _tapUntil = 0; // end a tap still being sent, so two quick taps are two Guide presses
        }
        // Once Guide is being held, Steam is Guide for the rest of this press: a late chord would
        // otherwise run its action after the game already saw Guide.
        if (steam && !_guideHeld)
        {
            foreach (var (button, action) in _chords)
            {
                if ((pressed & button) == 0) continue;
                actions.Add(action);
                _chordUsed = true;
                _latched |= button;
            }
        }
        _latched &= raw;

        var guide = false;
        long? wakeAt = null;
        if (steam)
        {
            if (!_chordUsed)
            {
                var holdAt = _steamDownAt + HoldTicks;
                if (now >= holdAt) guide = _guideHeld = true;
                else wakeAt = holdAt;
            }
        }
        else if (_steamHeld)
        {
            _steamHeld = false;
            if (!_chordUsed && now - _steamDownAt < HoldTicks) _tapUntil = now + TapTicks;
        }
        if (now < _tapUntil)
        {
            guide = true;
            wakeAt = _tapUntil;
        }

        var buttons = raw & ~_latched & ~ScButtons.Steam;
        if (guide) buttons |= ScButtons.Steam;
        return new(buttons, actions.ToArray(), wakeAt);
    }
}
