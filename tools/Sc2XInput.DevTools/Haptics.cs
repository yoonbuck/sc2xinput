using System.Diagnostics;
using System.Globalization;
using Sc2XInput.SteamController;

namespace Sc2XInput.DevTools;

/// <summary>
/// Plays pulse trains straight to the trackpads, bypassing the app and XInput, to tune haptics by feel. Each
/// segment is <c>pads:hz:onUs:ms</c> (pads L, R or LR; onUs is each pulse's on-time, which sets strength) or
/// <c>-:ms</c> for a pause. Trains are re-issued every 50 ms like the app's rumble.
/// </summary>
internal static class Haptics
{
    private const int ReissueMs = 50;

    public static int Run(IEnumerable<string> segments)
    {
        var plan = segments.Select(Parse).ToList();
        var devices = Program.OpenControllers((_, _) => { });
        try
        {
            foreach (var segment in plan)
            {
                Console.WriteLine($"{Program.Now()} {segment.Label}");
                var end = Stopwatch.GetTimestamp() + segment.Ms * Stopwatch.Frequency / 1000;
                for (var next = Stopwatch.GetTimestamp(); next < end; next += ReissueMs * Stopwatch.Frequency / 1000)
                {
                    foreach (var pad in segment.Pads)
                        foreach (var d in devices)
                            d.HapticPulse(pad, segment.OnUs, segment.OffUs, segment.Count);
                    // Sleep to a fixed schedule, so slow writes don't stretch the gap past the trains' overlap.
                    var due = Math.Min(end, next + ReissueMs * Stopwatch.Frequency / 1000);
                    Thread.Sleep((int)Math.Max(0, (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency));
                }
            }
            return 0;
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }

    private sealed record Segment(string Label, ScPad[] Pads, ushort OnUs, ushort OffUs, ushort Count, long Ms);

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    private static Segment Parse(string s)
    {
        var f = s.Split(':');
        if (f is ["-", var pause]) return new Segment($"pause {pause} ms", [], 0, 0, 0, Int(pause));
        if (f is not [var padsText, var hzText, var onUs, var ms])
            throw new FormatException($"expected pads:hz:onUs:ms or -:ms, got '{s}'");
        ScPad[] pads = padsText.ToUpperInvariant() switch
        {
            "L" => [ScPad.Left],
            "R" => [ScPad.Right],
            "LR" => [ScPad.Left, ScPad.Right],
            _ => throw new FormatException($"pads must be L, R or LR, got '{padsText}'"),
        };
        var hz = double.Parse(hzText, CultureInfo.InvariantCulture);
        if (!(hz >= 16 && hz <= 10_000)) throw new FormatException($"hz must be 16-10000 (the period is a 16-bit µs count), got '{hzText}'");
        var period = (int)Math.Round(1_000_000 / hz);
        var on = (ushort)Math.Clamp(Int(onUs), 1, period);
        var count = (ushort)Math.Ceiling(ReissueMs * 1000 * 1.5 / period); // overlap the next re-issue
        return new Segment($"{padsText.ToUpperInvariant()} {hzText} Hz, on {on} us / off {period - on} us, {ms} ms",
            pads, on, (ushort)(period - on), count, Int(ms));
    }
}
