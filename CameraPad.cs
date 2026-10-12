namespace Sc2XInput;

/// <summary>
/// Right-pad "camera" mode, like Steam's Joystick Mouse: the stick deflects in proportion to how fast the
/// finger moves, so the camera turns by an amount proportional to the swipe distance and stops when the
/// finger stops. Thread-safe: samples arrive on the reader thread, output is polled from a ticker.
/// </summary>
internal sealed class CameraPad
{
    private const double PadRadius = short.MaxValue;
    private const double InertiaStopSpeed = 0.5; // pad radii/s; end the coast instead of creeping along at the anti-deadzone
    private static readonly double TicksPerSecond = System.Diagnostics.Stopwatch.Frequency;

    private readonly Config _config;
    private readonly object _lock = new();
    // Touch samples covering the smoothing window, oldest first. The first one is the position at or
    // before the window start (or the touch-down position if the touch is younger than the window).
    private readonly List<(long t, double x, double y)> _history = [];
    private bool _touched;
    private double _inertiaX, _inertiaY; // pad radii/s, after a flick with trackball enabled
    private long _inertiaT;

    public CameraPad(Config config) => _config = config;

    private long Window => (long)(Math.Max(1, _config.CameraSmoothingMs) / 1000 * TicksPerSecond);

    /// <summary>True while output may change without new reports (finger down or inertia running).</summary>
    public bool IsActive
    {
        get { lock (_lock) return _touched || _inertiaX != 0 || _inertiaY != 0; }
    }

    public void Update(bool touched, short x, short y, long timestamp)
    {
        lock (_lock)
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
                    Trim(timestamp);
                    (_inertiaX, _inertiaY) = Velocity(timestamp);
                    _inertiaT = timestamp;
                }
                _history.Clear();
            }
            _touched = touched;
        }
    }

    /// <summary>Right-stick output at <paramref name="now"/>.</summary>
    public (short x, short y) Sample(long now)
    {
        lock (_lock)
        {
            double vx, vy;
            if (_touched)
            {
                Trim(now);
                (vx, vy) = Velocity(now);
            }
            else if (_inertiaX != 0 || _inertiaY != 0)
            {
                var decay = Math.Exp(-_config.CameraTrackballFriction * (now - _inertiaT) / TicksPerSecond);
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
    }

    private void Trim(long now)
    {
        var start = now - Window;
        while (_history.Count >= 2 && _history[1].t <= start) _history.RemoveAt(0);
    }

    // Displacement over the window divided by the full window length: a moving average of velocity,
    // which also ramps in smoothly from touch-down instead of spiking on the first delta.
    private (double, double) Velocity(long now)
    {
        if (_history.Count == 0) return (0, 0);
        var (_, x0, y0) = _history[0];
        var (_, x1, y1) = _history[^1];
        var seconds = Window / TicksPerSecond;
        return ((x1 - x0) / seconds, (y1 - y0) / seconds);
    }

    private (short, short) ToStick(double vx, double vy)
    {
        var speed = Math.Sqrt(vx * vx + vy * vy);
        var threshold = Math.Max(0, _config.CameraJitterThreshold);
        if (speed <= threshold) return (0, 0);

        // Start just past the game's deadzone so slow, precise movements still register.
        var anti = Math.Clamp(_config.CameraAntiDeadzone, 0, 0.95);
        var magnitude = anti + (1 - anti) * Math.Min(1, speed * _config.CameraSensitivity);
        // ...but fade in over [threshold, 2 * threshold] rather than jumping straight there, so a resting
        // thumb hovering around the threshold stays inside the game's deadzone instead of twitching past it.
        if (speed < 2 * threshold) magnitude *= (speed - threshold) / threshold;
        var k = magnitude / speed * short.MaxValue;
        return ((short)Math.Clamp(Math.Round(vx * k), -short.MaxValue, short.MaxValue),
                (short)Math.Clamp(Math.Round(vy * k), -short.MaxValue, short.MaxValue));
    }
}
