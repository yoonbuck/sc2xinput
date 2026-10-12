using System.Globalization;
using static Sc2XInput.Tests.Time;

namespace Sc2XInput.Tests;

/// <summary>
/// Replays a real right-pad recording (wireless dongle, reports every 8-12 ms) through CameraPad with
/// the default config, sampled at the 4 ms camera tick. Rows: t_ms, touched, x, y.
/// </summary>
public class RecordedPadReplayTests
{
    private static readonly (double t, bool touched, short x, short y)[] Recording =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "right-pad-recording.csv"))
            .Select(l => l.Split(','))
            .Select(p => (double.Parse(p[0], CultureInfo.InvariantCulture), p[1] == "1", short.Parse(p[2], CultureInfo.InvariantCulture), short.Parse(p[3], CultureInfo.InvariantCulture)))
            .ToArray();

    /// <summary>Output magnitudes (fraction of full deflection) at each tick inside [fromMs, toMs].</summary>
    private static List<double> Replay(double fromMs, double toMs)
    {
        var pad = new CameraPad(new Config());
        var outputs = new List<double>();
        var next = 0;
        for (var now = Recording[0].t; now < toMs; now += 4)
        {
            while (next < Recording.Length && Recording[next].t <= now)
            {
                var r = Recording[next++];
                pad.Update(r.touched, r.x, r.y, Ms(r.t));
            }
            var (sx, sy) = pad.Sample(Ms(now));
            if (now >= fromMs) outputs.Add(Math.Sqrt((double)sx * sx + (double)sy * sy) / short.MaxValue);
        }
        return outputs;
    }

    [Fact]
    public void RestingThumb_ProducesNoOutput() =>
        Assert.All(Replay(9700, 13000), o => Assert.Equal(0, o));

    [Fact]
    public void SlowDeliberateSlides_MostlyRegister()
    {
        var outputs = Replay(15830, 20526);
        Assert.True(outputs.Count(o => o > 0) > outputs.Count * 0.7, "slow slides should register most of the time");
        Assert.True(outputs.Max() < 1, "slow slides shouldn't saturate");
    }

    [Fact]
    public void Flicks_ReachFullDeflection() => Assert.Equal(1, Replay(25002, 26918).Max(), 3);
}
