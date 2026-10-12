using System.Diagnostics;

namespace Sc2XInput.Tests;

/// <summary>Synthetic time in <see cref="Stopwatch"/> ticks, the unit the code under test works in.</summary>
internal static class Time
{
    public static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
    public static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
