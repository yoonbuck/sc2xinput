using Sc2XInput.SteamController;

namespace Sc2XInput;

/// <summary>
/// State derived for the virtual pad's output: camera history, the post-switch mute and the last report
/// sent. Exists only while bridging as a gamepad, and is rebuilt when the pad is plugged in or the
/// right-pad mode changes, so nothing computed for an earlier output can leak into the next one.
/// </summary>
internal sealed class OutputStage
{
    private readonly Config _config;
    private readonly RightPadMode _mode;
    private readonly CameraPad _camera;

    /// <summary>Right pad ignored until the thumb lifts (the mode-switch chord is a click with the thumb down).</summary>
    public bool Muted { get; private set; }

    /// <summary>Last report handed to the shell; null when unknown (fresh stage, or the last submit failed).</summary>
    public XState? LastSubmitted { get; set; }

    public OutputStage(Config config, RightPadMode mode, bool muted)
    {
        _config = config;
        _mode = mode;
        _camera = new CameraPad(config);
        Muted = muted;
    }

    /// <summary>Feeds a report's right-pad sample to the camera, and ends the mute once the thumb lifts.</summary>
    public void Update(ScInputState state, long timestamp)
    {
        if (Muted)
        {
            if (state.RPadTouched) return;
            Muted = false;
        }
        if (_mode == RightPadMode.Camera) _camera.Update(state.RPadTouched, state.RPadX, state.RPadY, timestamp);
    }

    public XState Build(ScInputState state, ScButtons visible, long now)
    {
        var x = Mapper.Map(state, visible, _config, Muted ? RightPadMode.None : _mode);
        if (_mode != RightPadMode.Camera || Muted) return x;
        var (rx, ry) = _camera.Sample(now);
        return x with { RightX = rx, RightY = ry };
    }

    /// <summary>
    /// Whether output can change without new input: camera inertia or fade-out still running, or the last
    /// report still has the right stick deflected (the centring report hasn't gone out yet).
    /// </summary>
    public bool NeedsTicks(long now) =>
        _mode == RightPadMode.Camera && !Muted && _camera.IsActive(now)
        || LastSubmitted is not { RightX: 0, RightY: 0 };
}
