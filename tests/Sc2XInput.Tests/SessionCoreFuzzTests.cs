using B = Sc2XInput.SteamController.ScButtons;

namespace Sc2XInput.Tests;

/// <summary>
/// Random event sequences against <see cref="SessionCore"/>, with effects failing at random. The harness
/// checks the session invariants after every step; this adds a few that need the whole sequence.
/// </summary>
public class SessionCoreFuzzTests
{
    private static readonly B[] Buttons = [B.Steam, B.Steam, B.Y, B.Y, B.RPadClick, B.Back, B.A, B.LGrip, B.LPadClick];

    // 200 in CI; set SC2X_FUZZ_SEEDS for a longer local run.
    public static TheoryData<int> Seeds =>
        new(Enumerable.Range(1, int.TryParse(Environment.GetEnvironmentVariable("SC2X_FUZZ_SEEDS"), out var n) ? n : 200));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomEventSequences_KeepTheInvariants(int seed)
    {
        var rnd = new Random(seed);
        var config = new Config
        {
            RightPadMode = (RightPadMode)rnd.Next(3),
            CameraTrackball = rnd.Next(2) == 0,
            Rumble = rnd.Next(4) != 0,
        };
        var h = new CoreHarness(config, captured: rnd.Next(3) != 0);
        var failRate = rnd.NextDouble() * 0.15;
        var failing = true;
        h.Fail = _ => failing && rnd.NextDouble() < failRate;

        try
        {
            Run(rnd, h, () => failing = false);
        }
        catch (Exception ex)
        {
            // Enough context to reproduce by hand.
            var effects = string.Join("\n", h.Executed.TakeLast(12).Select(e => $"  {Time.ToMs(e.At):F0} ms {e.Effect}"));
            var log = string.Join("\n", h.Messages.TakeLast(8).Select(m => $"  {m.Level}: {m.Text}"));
            throw new Xunit.Sdk.XunitException($"seed {seed}: {ex.Message}\nstate {h.Core.State}\nlast effects:\n{effects}\nlast log:\n{log}");
        }
    }

    private static void Run(Random rnd, CoreHarness h, Action stopFailing)
    {
        var held = B.None;
        var touch = false;
        short rx = 0, ry = 0;
        for (var step = 0; step < 400; step++)
        {
            h.Advance(rnd.Next(0, 40));
            var roll = rnd.Next(100);
            if (roll < 65)
            {
                var before = held;
                if (rnd.Next(3) > 0) held ^= Buttons[rnd.Next(Buttons.Length)];
                if (rnd.Next(4) == 0) touch = !touch;
                rx = (short)Math.Clamp(rx + rnd.Next(-6000, 6001), -32767, 32767);
                ry = (short)Math.Clamp(ry + rnd.Next(-6000, 6001), -32767, 32767);

                var captureLogs = h.Messages.Count(m => m.Text.StartsWith("capture o"));
                h.Report(held, touch, rx, ry);
                var toggled = h.Messages.Count(m => m.Text.StartsWith("capture o")) - captureLogs;
                // Capture only ever toggles on a fresh Y press, at most once per press.
                Assert.True(toggled == 0 || (held & ~before & B.Y) != 0, $"capture toggled without a new Y press (step {step})");
                Assert.True(toggled <= 1);
            }
            else if (roll < 70) h.Step(new Disconnected());
            else if (roll < 77) h.Step(new Connected());
            else if (roll < 85) h.Step(new Rumble(h.Generation - rnd.Next(2), (byte)rnd.Next(256), (byte)rnd.Next(256)));
            else if (roll < 90) h.Step(new PlayerIndexResult(h.Generation - rnd.Next(2), rnd.Next(3) == 0 ? null : rnd.Next(4)));
            else h.Advance(rnd.Next(0, 2000));
        }

        var awake = h.Core.State is not (SessionCore.Asleep or SessionCore.Closed);
        stopFailing();
        h.Step(new Close());
        Assert.True(h.Core.Status.IsClosed);
        Assert.False(h.PadConnected);
        if (awake) Assert.True(h.LizardOn, "closing left the controller without its mouse/keyboard mode");
        Assert.Null(h.Core.NextDeadline);
    }
}

/// <summary>Keeps the core free of I/O, threads and the clock, so it stays deterministic and testable.</summary>
public class SessionCorePurityTests
{
    private static readonly string[] CoreFiles = ["SessionCore.cs", "SessionEvents.cs", "InputTracker.cs", "OutputStage.cs"];

    private static readonly string[] Forbidden =
    [
        "ScDevice", "Nefarius", "ViGEm", "Log.", "Thread", "Task.", "Timer", "lock (", "Stopwatch.GetTimestamp",
        "DateTime", "Environment.TickCount", "Console.",
    ];

    public static TheoryData<string> Files => new(CoreFiles);

    [Theory]
    [MemberData(nameof(Files))]
    public void CoreFile_HasNoIoThreadsOrClock(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "sc2xinput.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir.FullName, file));
        Assert.All(Forbidden, word => Assert.DoesNotContain(word, source));
    }
}
