using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Sc2XInput.Native;
using Sc2XInput.SteamController;

namespace Sc2XInput.DevTools;

/// <summary>Hardware diagnostics for verifying sc2xinput on a real controller. Each command prints timestamped lines.</summary>
internal static class Program
{
    private const string Usage = """
        sc2xinput-devtools <command>

          xinput [seconds]                 print XInput state changes for all slots, including Guide (default 60 s)
          rumble <large> <small> <ms>      rumble every connected XInput slot (motor levels 0-255)
          inputlog [seconds]               Steam Controller button edges + keyboard/mouse input coming from the
                                           controller itself, on one timeline (default 120 s). Use with capture
                                           off to map what each button sends in mouse/keyboard mode, or with
                                           capture on to check nothing leaks.
          record-pad <file.csv> [seconds]  record right-pad samples as t_ms,touched,x,y (default 60 s); the format
                                           of tests/Sc2XInput.Tests/Fixtures/right-pad-recording.csv
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        double Seconds(int i, double fallback)
        {
            var s = args.Length > i ? double.Parse(args[i], CultureInfo.InvariantCulture) : fallback;
            return s > 0 ? s : throw new ArgumentOutOfRangeException(nameof(args), "seconds must be positive");
        }
        switch (args.FirstOrDefault())
        {
            case "xinput": return XInput.Watch(Seconds(1, 60));
            case "rumble" when args.Length == 4 && int.Parse(args[3]) >= 0:
                return XInput.Rumble(byte.Parse(args[1]), byte.Parse(args[2]), int.Parse(args[3]));
            case "inputlog": return InputLog.Run(Seconds(1, 120));
            case "record-pad" when args.Length >= 2: return RecordPad(args[1], Seconds(2, 60));
            default:
                Console.WriteLine(Usage);
                return args.Length == 0 ? 0 : 1;
        }
    }

    internal static string Now() => $"{DateTime.Now:HH:mm:ss.fff}";

    /// <summary>Opens every Steam Controller vendor interface and calls <paramref name="onReport"/> for input reports.</summary>
    internal static List<ScDevice> OpenControllers(Action<ScDevice, ReadOnlySpan<byte>> onReport)
    {
        var devices = Win32.GetHidDevicePaths().Select(ScDevice.TryOpen).OfType<ScDevice>().ToList();
        foreach (var d in devices)
        {
            d.ReportReceived += (dev, m) => { if (m.Span[2] == ScProtocol.EvInput) onReport(dev, m.Span); };
            d.Start();
        }
        Console.WriteLine($"{Now()} listening on {devices.Count} Steam Controller interface(s)");
        return devices;
    }

    private static int RecordPad(string file, double seconds)
    {
        // Record to a temp file and only replace the target if something was captured, so running this with
        // no awake controller (the dongle exposes its slots regardless) can't wipe an existing fixture.
        var temp = file + ".recording";
        var samples = 0;
        var states = new Dictionary<ScDevice, ScInputState>(); // one per controller; the fixture assumes one
        using (var output = new StreamWriter(temp) { AutoFlush = true })
        {
            var start = Stopwatch.GetTimestamp();
            var gate = new object();
            var devices = OpenControllers((device, r) =>
            {
                lock (gate)
                {
                    if (!states.TryGetValue(device, out var state)) states[device] = state = new ScInputState();
                    state.Apply(r);
                    var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{ms:F2},{(state.RPadTouched ? 1 : 0)},{state.RPadX},{state.RPadY}"));
                    samples++;
                }
            });
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            devices.ForEach(d => d.Dispose()); // joins the reader threads, so no more writes after this
        }

        if (samples == 0)
        {
            File.Delete(temp);
            Console.WriteLine($"no input reports received; {file} left unchanged (is the controller awake?)");
            return 1;
        }
        File.Move(temp, file, overwrite: true);
        if (states.Count > 1) Console.WriteLine("warning: more than one controller reported; rows are interleaved");
        Console.WriteLine($"wrote {samples} samples to {file}");
        return 0;
    }
}

internal static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vibration { public ushort Left, Right; }

    // Ordinal 100 is XInputGetStateEx: same as XInputGetState, but it doesn't mask out the Guide button.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint GetStateEx(uint index, out State state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static extern uint SetState(uint index, ref Vibration vibration);

    private static readonly (ushort bit, string name)[] ButtonNames =
    [
        (0x0001, "Up"), (0x0002, "Down"), (0x0004, "Left"), (0x0008, "Right"), (0x0010, "Start"), (0x0020, "Back"),
        (0x0040, "LS"), (0x0080, "RS"), (0x0100, "LB"), (0x0200, "RB"), (0x0400, "Guide"),
        (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y"),
    ];

    private static string Names(ushort buttons) =>
        buttons == 0 ? "-" : string.Join("+", ButtonNames.Where(b => (buttons & b.bit) != 0).Select(b => b.name));

    public static int Watch(double seconds)
    {
        var last = new string?[4];
        var end = DateTime.Now.AddSeconds(seconds);
        Console.WriteLine($"{Program.Now()} watching XInput slots for {seconds} s");
        while (DateTime.Now < end)
        {
            for (uint i = 0; i < 4; i++)
            {
                var line = GetStateEx(i, out var s) == 0
                    ? $"slot {i}: {Names(s.Buttons),-14} LT={s.LT,3} RT={s.RT,3} L=({s.LX,6},{s.LY,6}) R=({s.RX,6},{s.RY,6})"
                    : $"slot {i}: not connected";
                if (line == last[i]) continue;
                if (last[i] != null || !line.EndsWith("not connected")) Console.WriteLine($"{Program.Now()} {line}");
                last[i] = line;
            }
            Thread.Sleep(5);
        }
        return 0;
    }

    public static int Rumble(byte large, byte small, int ms)
    {
        var slots = Enumerable.Range(0, 4).Select(i => (uint)i).Where(i => GetStateEx(i, out _) == 0).ToList();
        if (slots.Count == 0) { Console.WriteLine("no XInput controllers connected"); return 1; }
        var on = new Vibration { Left = (ushort)(large * 257), Right = (ushort)(small * 257) };
        // XInput rumble is a level that persists until changed, so always turn it off, including on Ctrl+C.
        void Stop() { var off = new Vibration(); foreach (var i in slots) SetState(i, ref off); }
        ConsoleCancelEventHandler onCancel = (_, _) => Stop();
        Console.CancelKeyPress += onCancel;
        try
        {
            foreach (var i in slots) SetState(i, ref on);
            Thread.Sleep(ms);
        }
        finally
        {
            Stop();
            Console.CancelKeyPress -= onCancel;
        }
        Console.WriteLine($"rumbled slot(s) {string.Join(",", slots)}: large={large} small={small} for {ms} ms");
        return 0;
    }
}
