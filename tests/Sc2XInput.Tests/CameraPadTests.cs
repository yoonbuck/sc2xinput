using static Sc2XInput.Tests.Time;

namespace Sc2XInput.Tests;

public class CameraPadTests
{
    private const double Radius = short.MaxValue;

    // Linear config: no anti-deadzone or jitter threshold, so output / (0.5 * 32767) reads finger speed.
    private static Config Linear() => new() { CameraAntiDeadzone = 0, CameraJitterThreshold = 0, CameraSensitivity = 0.5 };

    private static double Mag((short x, short y) v) => Math.Sqrt((double)v.x * v.x + (double)v.y * v.y) / Radius;

    /// <summary>Swipes along +X at <paramref name="speed"/> radii/s with reports every <paramref name="intervalMs"/>.</summary>
    private static void Swipe(CameraPad pad, double speed, double fromMs, double toMs, Func<int, double> intervalMs, double startX = -0.5)
    {
        var i = 0;
        for (var t = fromMs; t <= toMs; t += intervalMs(i++))
            pad.Update(true, (short)((startX + speed * (t - fromMs) / 1000) * Radius), 0, Ms(t));
    }

    [Fact]
    public void StillFinger_GivesNoOutput()
    {
        var pad = new CameraPad(new Config());
        for (var i = 0; i < 50; i++) pad.Update(true, 1000, 1000, Ms(i * 10));
        Assert.Equal((0, 0), pad.Sample(Ms(500)));
    }

    public static TheoryData<string> Cadences => ["8", "10", "12", "random"];

    [Theory]
    [MemberData(nameof(Cadences))]
    public void Speed_DoesNotDependOnReportCadence(string cadence)
    {
        var rnd = new Random(7);
        Func<int, double> interval = cadence == "random" ? _ => 8 + rnd.NextDouble() * 4 : _ => double.Parse(cadence);
        var pad = new CameraPad(Linear());
        var speeds = new List<double>();
        double next = 0;
        var i = 0;
        for (double now = 0; now < 400; now += 4) // sampled at the 4 ms camera tick
        {
            while (next <= now)
            {
                pad.Update(true, (short)((-0.2 + 1.0 * next / 1000) * Radius), 0, Ms(next));
                next += interval(i++);
            }
            if (now > 100) speeds.Add(pad.Sample(Ms(now)).x / Radius / 0.5);
        }
        // A steady 1 radius/s swipe reads 1.0 regardless of report timing, with no ripple between reports.
        Assert.InRange(speeds.Min(), 0.995, 1.005);
        Assert.InRange(speeds.Max(), 0.995, 1.005);
    }

    [Fact]
    public void SameDistance_AtDifferentSpeeds_GivesSameTotalTurn()
    {
        double Integral(double speed)
        {
            // Low enough sensitivity that even 4 radii/s stays below full deflection (saturation would
            // cap the turn rate and break proportionality, which is expected).
            var pad = new CameraPad(new Config { CameraAntiDeadzone = 0, CameraJitterThreshold = 0, CameraSensitivity = 0.2 });
            double x = -0.5, sum = 0;
            var t = 0.0;
            for (; x < 0.5; t += 4, x += speed * 0.004) { pad.Update(true, (short)(x * Radius), 0, Ms(t)); sum += pad.Sample(Ms(t)).x / Radius * 0.004; }
            for (var j = 0; j < 20; j++, t += 4) { pad.Update(true, (short)(x * Radius), 0, Ms(t)); sum += pad.Sample(Ms(t)).x / Radius * 0.004; }
            return sum;
        }
        var baseline = Integral(1);
        Assert.InRange(Integral(2), baseline * 0.97, baseline * 1.03);
        Assert.InRange(Integral(4), baseline * 0.97, baseline * 1.03);
    }

    [Fact]
    public void ReportsStoppingMidTouch_HoldsThenFadesToZero_ThenGoesInactive()
    {
        var pad = new CameraPad(new Config());
        Swipe(pad, 2, 0, 200, _ => 10);
        var held = pad.Sample(Ms(220));
        Assert.True(held.x > 0);
        Assert.Equal(held, pad.Sample(Ms(200))); // held through the 20 ms report gap
        Assert.True(pad.Sample(Ms(240)).x < held.x); // fading
        Assert.Equal((0, 0), pad.Sample(Ms(249)));
        Assert.True(pad.IsActive(Ms(249)));
        Assert.False(pad.IsActive(Ms(251))); // lets the ticker park

        pad.Update(true, 0, 0, Ms(5000));
        Assert.True(pad.IsActive(Ms(5001))); // the next report wakes it
    }

    [Fact]
    public void TouchDown_RampsInWithoutASpike()
    {
        // Off-centre touch-down, then a steady 2 radii/s swipe. Positions before touch-down count as the
        // touch-down point, so speed ramps up over the first window instead of spiking (e.g. from the
        // pad centre) or jumping straight to the steady value.
        var pad = new CameraPad(new Config { CameraAntiDeadzone = 0, CameraJitterThreshold = 0, CameraSensitivity = 0.2 });
        var outputs = new List<double>();
        for (var i = 0; i <= 6; i++)
        {
            pad.Update(true, (short)((0.3 + 2 * i * 0.010) * Radius), 0, Ms(i * 10));
            outputs.Add(Mag(pad.Sample(Ms(i * 10))));
        }
        Assert.Equal(0, outputs[0]); // nothing at touch-down itself
        var steady = outputs[^1];
        Assert.InRange(steady, 0.39, 0.41); // 2 radii/s × 0.2
        Assert.True(outputs[1] < steady * 0.5, $"first report already at {outputs[1]:F3} of {steady:F3}");
        for (var i = 1; i < outputs.Count; i++) Assert.True(outputs[i] >= outputs[i - 1], $"output dropped at report {i}");
    }

    [Fact]
    public void Direction_FollowsTheSwipe()
    {
        var pad = new CameraPad(new Config());
        for (var i = 0; i <= 20; i++) pad.Update(true, (short)(-i * 300), (short)(i * 300), Ms(i * 4));
        var (x, y) = pad.Sample(Ms(80));
        Assert.True(x < 0 && y > 0 && Math.Abs(x + y) < 50, $"({x},{y})"); // up-left, 45°
    }

    [Fact]
    public void JitterBelowThreshold_GivesNoOutput()
    {
        var pad = new CameraPad(new Config()); // threshold 0.15 radii/s
        var rnd = new Random(1);
        for (var i = 0; i < 200; i++)
        {
            pad.Update(true, (short)(5000 + rnd.Next(-30, 31)), (short)rnd.Next(-30, 31), Ms(i * 10));
            Assert.Equal((0, 0), pad.Sample(Ms(i * 10)));
        }
    }

    [Fact]
    public void JustAboveThreshold_FadesIn_InsteadOfJumpingToTheAntiDeadzone()
    {
        double At(double speed)
        {
            var pad = new CameraPad(new Config());
            Swipe(pad, speed, 0, 200, _ => 10, startX: 0);
            return Mag(pad.Sample(Ms(200)));
        }
        var anti = new Config().CameraAntiDeadzone;
        Assert.InRange(At(0.2), 0.001, anti * 0.5); // between threshold (0.15) and 2× threshold
        Assert.True(At(0.35) >= anti); // past 2× threshold: full anti-deadzone
    }

    [Fact]
    public void Trackball_CoastsAfterFlick_AndTouchStopsIt()
    {
        var pad = new CameraPad(new Config { CameraTrackball = true });
        Swipe(pad, 4, 0, 100, _ => 10, startX: 0);
        pad.Update(false, 0, 0, Ms(110));
        var justAfter = Mag(pad.Sample(Ms(112)));
        var later = Mag(pad.Sample(Ms(410)));
        Assert.True(justAfter > 0 && later > 0 && later < justAfter);
        Assert.True(pad.IsActive(Ms(410)));
        Assert.Equal((0, 0), pad.Sample(Ms(3110))); // friction brings it to a stop
        Assert.False(pad.IsActive(Ms(3110)));

        Swipe(pad, 4, 4000, 4100, _ => 10, startX: 0);
        pad.Update(false, 0, 0, Ms(4110));
        pad.Update(true, 0, 0, Ms(4200)); // touching the pad stops the ball
        Assert.Equal((0, 0), pad.Sample(Ms(4201)));
        // Sample ignores inertia while touched, so check the ball itself is gone: once the touch's
        // reports go stale, nothing is left to keep the session active.
        Assert.False(pad.IsActive(Ms(4260)));
    }

    [Fact]
    public void NoTrackball_StopsOnLift()
    {
        var pad = new CameraPad(new Config());
        Swipe(pad, 6, 0, 100, _ => 10, startX: 0);
        pad.Update(false, 0, 0, Ms(110));
        Assert.Equal((0, 0), pad.Sample(Ms(111)));
        Assert.False(pad.IsActive(Ms(111)));
    }

    [Fact]
    public void ZeroFriction_IsFloored_SoACoastStillEnds()
    {
        var pad = new CameraPad(new Config { CameraTrackball = true, CameraTrackballFriction = 0 });
        Swipe(pad, 3, 0, 100, _ => 10, startX: 0);
        pad.Update(false, 0, 0, Ms(110));
        Assert.Equal((0, 0), pad.Sample(Ms(120_000)));
    }

    [Fact]
    public void NegativeSensitivity_DoesNotInvertOutput()
    {
        var pad = new CameraPad(new Config { CameraSensitivity = -1 });
        Swipe(pad, 2, 0, 200, _ => 10, startX: 0);
        Assert.True(pad.Sample(Ms(200)).x >= 0);
    }

    [Fact]
    public void HugeSmoothingWindow_IsClamped_AndStaysActiveMidTouch()
    {
        var pad = new CameraPad(new Config { CameraSmoothingMs = 1e15 });
        Swipe(pad, 2, 0, 200, _ => 10, startX: 0);
        Assert.True(pad.IsActive(Ms(201)));
    }

    [Fact]
    public void Reset_ClearsTouchAndInertia()
    {
        var pad = new CameraPad(new Config { CameraTrackball = true });
        Swipe(pad, 4, 0, 100, _ => 10, startX: 0);
        pad.Update(false, 0, 0, Ms(110));
        Assert.True(pad.IsActive(Ms(120)));
        pad.Reset();
        Assert.False(pad.IsActive(Ms(120)));
        Assert.Equal((0, 0), pad.Sample(Ms(120)));
    }
}
