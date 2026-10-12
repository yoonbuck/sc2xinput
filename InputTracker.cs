using Sc2XInput.SteamController;

namespace Sc2XInput;

/// <summary>
/// State derived from the input stream itself: the accumulated report state and Steam-chord tracking.
/// Lives as long as the link does; SessionCore replaces it only when the link drops. It deliberately
/// survives capture toggles and right-pad mode switches, so a chord button that is still held stays
/// latched instead of reading as a new press.
/// </summary>
internal sealed class InputTracker
{
    private readonly SteamChords _chords;
    private readonly ScButtons _heldBeforeDrop;

    public ScInputState State { get; } = new();

    /// <summary>What this link last knew to be held: the current buttons, or, before any report, those carried over.</summary>
    public ScButtons KnownHeld => HasInput ? State.Buttons : _heldBeforeDrop;

    /// <summary>Whether any report has arrived on this link yet. Until one does, which buttons are held is unknown.</summary>
    public bool HasInput { get; private set; }

    /// <summary>Buttons the game should see after chord filtering; <see cref="ScButtons.Steam"/> means Guide.</summary>
    public ScButtons Visible { get; private set; }

    /// <summary>When the chord logic needs to run again without new input (Guide hold/tap timing).</summary>
    public long? WakeAt { get; private set; }

    /// <param name="heldBeforeDrop">Buttons held when the previous link dropped; see <see cref="Apply"/>.</param>
    public InputTracker(Config config, ScButtons heldBeforeDrop = ScButtons.None)
    {
        _chords = new SteamChords(config.SteamChords);
        _heldBeforeDrop = heldBeforeDrop;
    }

    /// <summary>Applies a report; returns the chord actions it triggered, in order.</summary>
    public ChordAction[] Apply(byte[] report, long timestamp)
    {
        State.Apply(report);
        // A button held through a link drop isn't a new press when the link comes back; otherwise holding
        // Steam+Y through a reconnect would run the chord again. Buttons pressed since are real presses.
        if (!HasInput) _chords.Prime(State.Buttons & _heldBeforeDrop);
        HasInput = true;
        return Process(timestamp);
    }

    /// <summary>Re-evaluates chord timing with unchanged input. Never triggers actions.</summary>
    public void Reprocess(long now) => Process(now);

    private ChordAction[] Process(long now)
    {
        var r = _chords.Process(State.Buttons, now);
        Visible = r.Buttons;
        WakeAt = r.WakeAt;
        return r.Actions;
    }
}
