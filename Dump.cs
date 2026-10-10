using Sc2XInput.Native;
using Sc2XInput.SteamController;

namespace Sc2XInput;

/// <summary>Prints raw reports so the protocol decoding can be checked against real hardware.</summary>
internal static class Dump
{
    public static int Run(ManualResetEventSlim stop)
    {
        var devices = Win32.GetHidDevicePaths().Select(ScDevice.TryOpen).OfType<ScDevice>().ToList();
        if (devices.Count == 0)
        {
            Log.Error("No Steam Controller interfaces found.");
            return 1;
        }

        var state = new Dictionary<ScDevice, ScInputState>();
        var lastInput = new Dictionary<ScDevice, string>();
        foreach (var dev in devices)
        {
            Log.Info($"[{dev.Name}] {dev.Path}");
            state[dev] = new ScInputState();
            dev.ReportReceived += (d, mem) =>
            {
                var r = mem.Span;
                if (r[2] == ScProtocol.EvInput)
                {
                    // Only print when the interesting bytes change; reports stream continuously.
                    var key = Convert.ToHexString(r[8..24]);
                    lock (lastInput)
                    {
                        if (lastInput.GetValueOrDefault(d) == key) return;
                        lastInput[d] = key;
                    }
                    var s = state[d];
                    s.Apply(r);
                    Log.Info($"[{d.Name}] in {key[..6]} {key[6..10]} {key[16..]} | {s.Buttons} LT={s.LeftTrigger} RT={s.RightTrigger} " +
                             $"stick=({s.StickX},{s.StickY}) lpad=({s.LPadX},{s.LPadY}){(s.LPadTouched ? "*" : "")} " +
                             $"rpad=({s.RPadX},{s.RPadY}){(s.RPadTouched ? "*" : "")}");
                }
                else
                {
                    Log.Info($"[{d.Name}] type 0x{r[2]:X2}: {Convert.ToHexString(r[..16])}");
                }
            };
            dev.Closed += d => Log.Info($"[{d.Name}] closed");
            dev.Start();
            if (dev.IsWireless) dev.RequestConnectionStatus();
        }

        Log.Info("Dumping reports (lizard mode left on). Ctrl+C to quit.");
        stop.Wait();
        foreach (var dev in devices) dev.Dispose();
        return 0;
    }
}
