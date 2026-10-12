namespace Sc2XInput;

/// <summary>
/// Right-pad "camera" mode, like Steam's Joystick Mouse: the stick deflects in proportion to how fast the
/// finger moves, so the camera turns by an amount proportional to the swipe distance and stops when the
/// finger stops. Not thread-safe: owned by one session's loop.
/// </summary>
internal sealed class CameraPad
{
    private const double PadRadius = short.MaxValue;
    private const double InertiaStopSpeed = 0.5; // pad radii/s; end the coast instead of creeping along at the anti-deadzone
    private const double ReportGapSeconds = 0.02; // longer than the 8-12 ms report interval: reports have stopped
    private static readonly double TicksPerSecond = System.Diagnostics.Stopwatch.Frequency;

    private readonly Config _config;
    // Touch samples covering the smoothing window ending at the newest sample, oldest first. The first one
    // is at or before the window start (or is the touch-down position if the touch is younger than that).
    private readonly List<(long t, double x, double y)> _history = [];
    private bool _touched;
    private double _inertiaX, _inertiaY; // pad radii/s, after a flick with trackball enabled
    private long _inertiaT;

    public CameraPad(Config config) => _config = config;

    private long Window => (long)(Math.Clamp(_config.CameraSmoothingMs, 1, 1000) / 1000 * TicksPerSecond);

    /// <summary>Forget the touch and any trackball motion, e.g. when the controller disconnects mid-swipe.</summary>
    public void Reset()
    {
        _history.Clear();
        _touched = false;
        _inertiaX = _inertiaY = 0;
    }

    /// <summary>
    /// True while output may change without new reports: trackball inertia is running, or a finger is down
    /// and still reporting. A touch whose reports stopped has faded to zero after the report gap plus one
    /// window, so it no longer counts, which lets the session go idle until the next report.
    /// </summary>
    public bool IsActive(long now)
    {
        if (_inertiaX != 0 || _inertiaY != 0) return true;
        if (!_touched || _history.Count == 0) return false;
        return now - _history[^1].t < (long)(ReportGapSeconds * TicksPerSecond) + Window;
    }

    public void Update(bool touched, short x, short y, long timestamp)
    {
        if (touched)
        {
            if (!_touched)
            {
                _history.Clear();
                _inertiaX = _inertiaY = 0; // touching the pad stops the ball
            }
            _history.Add((timestamp, x / PadRadius, y / PadRadius));
            Trim(timestamp);
        }
        else if (_touched)
        {
            if (_config.CameraTrackball)
            {
                (_inertiaX, _inertiaY) = Velocity(timestamp);
                _inertiaT = timestamp;
            }
            _history.Clear();
        }
        _touched = touched;
    }

    /// <summary>Right-stick output at <paramref name="now"/>.</summary>
    public (short x, short y) Sample(long now)
    {
        double vx, vy;
        if (_touched)
        {
            (vx, vy) = Velocity(now);
        }
        else if (_inertiaX != 0 || _inertiaY != 0)
        {
            // Floor the friction: at 0 or below a flick would spin (or speed up) forever.
            var friction = Math.Max(0.1, _config.CameraTrackballFriction);
            var decay = Math.Exp(-friction * (now - _inertiaT) / TicksPerSecond);
            _inertiaX *= decay;
            _inertiaY *= decay;
            _inertiaT = now;
            if (Math.Sqrt(_inertiaX * _inertiaX + _inertiaY * _inertiaY) < InertiaStopSpeed)
                _inertiaX = _inertiaY = 0;
            (vx, vy) = (_inertiaX, _inertiaY);
        }
        else
        {
            return (0, 0);
        }
        return ToStick(vx, vy);
    }

    private void Trim(long latest)
    {
        var start = latest - Window;
        while (_history.Count >= 2 && _history[1].t <= start) _history.RemoveAt(0);
    }

    // Displacement over the window ending at the newest sample, divided by the window length: a moving
    // average of velocity. The window start is interpolated between the samples around it, so the result
    // doesn't depend on report cadence (8-12 ms over the dongle), and the window is anchored to the newest
    // sample rather than to now, so output doesn't ripple between reports. Positions before touch-down read
    // as the touch-down point, which ramps the speed in instead of spiking on the first delta.
    private (double, double) Velocity(long now)
    {
        if (_history.Count == 0) return (0, 0);
        var (t1, x1, y1) = _history[^1];
        var (ta, x0, y0) = _history[0];
        var start = t1 - Window;
        if (_history.Count >= 2 && ta < start)
        {
            var (tb, xb, yb) = _history[1];
            var f = (double)(start - ta) / (tb - ta);
            x0 += (xb - x0) * f;
            y0 += (yb - y0) * f;
        }
        var seconds = Window / TicksPerSecond;

        // If reports stop arriving mid-touch, fade out over a window rather than holding the last speed.
        var silent = (now - t1) / TicksPerSecond - ReportGapSeconds;
        var fade = Math.Clamp(1 - silent / seconds, 0, 1);
        return ((x1 - x0) / seconds * fade, (y1 - y0) / seconds * fade);
    }

    private (short, short) ToStick(double vx, double vy)
    {
        var speed = Math.Sqrt(vx * vx + vy * vy);
        var threshold = Math.Max(0, _config.CameraJitterThreshold);
        if (speed <= threshold) return (0, 0);

        // Start just past the game's deadzone so slow, precise movements still register.
        var anti = Math.Clamp(_config.CameraAntiDeadzone, 0, 0.95);
        var magnitude = anti + (1 - anti) * Math.Min(1, speed * Math.Max(0, _config.CameraSensitivity));
        // ...but fade in over [threshold, 2 * threshold] rather than jumping straight there, so a resting
        // thumb hovering around the threshold stays inside the game's deadzone instead of twitching past it.
        if (speed < 2 * threshold) magnitude *= (speed - threshold) / threshold;
        var k = magnitude / speed * short.MaxValue;
        return ((short)Math.Clamp(Math.Round(vx * k), -short.MaxValue, short.MaxValue),
                (short)Math.Clamp(Math.Round(vy * k), -short.MaxValue, short.MaxValue));
    }
}
