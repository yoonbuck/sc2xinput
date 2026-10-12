using System.Diagnostics;
using Sc2XInput.Native;

namespace Sc2XInput.Tests;

public class HighResWaitTests
{
    private static long In(double ms) => Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);

    [Fact]
    public void Deadline_IsMetToTheMillisecond_NotTheSystemTick()
    {
        using var wait = new HighResWait("test");
        using var never = new ManualResetEvent(false);
        var elapsed = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            Assert.Equal(-1, wait.Wait([never], In(4)));
            elapsed.Add(sw.Elapsed.TotalMilliseconds);
        }
        // The 4 ms camera tick needs better than the default ~15.6 ms timer resolution.
        Assert.True(elapsed.Min() >= 3.5, $"woke early: {elapsed.Min():F2} ms");
        Assert.True(elapsed.Average() < 8, $"mean {elapsed.Average():F2} ms");
    }

    [Fact]
    public void Signal_EndsTheWaitEarly_AndReportsWhichHandle()
    {
        using var wait = new HighResWait("test");
        using var a = new ManualResetEvent(false);
        using var b = new ManualResetEvent(false);
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(20); b.Set(); });
        var sw = Stopwatch.StartNew();
        Assert.Equal(1, wait.Wait([a, b], In(2000)));
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public void PassedDeadline_ReturnsImmediately()
    {
        using var wait = new HighResWait("test");
        using var never = new ManualResetEvent(false);
        Assert.Equal(-1, wait.Wait([never], Stopwatch.GetTimestamp() - 1));
    }

    [Fact]
    public void NoDeadline_WaitsForAHandleOnly()
    {
        using var wait = new HighResWait("test");
        using var signalled = new ManualResetEvent(true);
        Assert.Equal(0, wait.Wait([signalled], null));
    }

    [Fact]
    public void ATimerFromAnEarlierWait_DoesNotEndALaterOneEarly()
    {
        using var wait = new HighResWait("test");
        using var signalled = new ManualResetEvent(true);
        using var never = new ManualResetEvent(false);
        Assert.Equal(0, wait.Wait([signalled], In(5))); // arms the timer, returns on the signal
        Thread.Sleep(15);                                // the old arm fires meanwhile
        var sw = Stopwatch.StartNew();
        Assert.Equal(-1, wait.Wait([never], In(30)));
        Assert.True(sw.Elapsed.TotalMilliseconds >= 25, $"returned after {sw.Elapsed.TotalMilliseconds:F1} ms");
    }
}
