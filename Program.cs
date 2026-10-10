using System.Diagnostics;
using System.Runtime.InteropServices;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Sc2XInput.Native;
using Sc2XInput.SteamController;

namespace Sc2XInput;

internal static class Program
{
    private const string Usage = """
        sc2xinput - exposes Steam Controllers as virtual Xbox 360 (XInput) controllers

          sc2xinput                 run in the notification area (tray)
          sc2xinput --console       run in a console window instead (Ctrl+C to quit)
          sc2xinput --config PATH   use a specific config file (default: sc2xinput.json next to the exe)
          sc2xinput --dump          print raw controller reports (for debugging the protocol)
          sc2xinput --restore       turn the controllers' built-in mouse/keyboard mode back on
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        var consoleMode = args.Any(a => a is "--console" or "--dump" or "--restore" or "--help" or "-h");
        if (consoleMode) Win32.EnsureConsole();

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args.Contains("--restore"))
            return Restore();

        if (args.Contains("--dump"))
        {
            using var stop = new ManualResetEventSlim(false);
            using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Set(); });
            return Dump.Run(stop);
        }

        using var instance = new Mutex(true, @"Local\sc2xinput", out var isFirstInstance);
        if (!isFirstInstance)
            return Fail("sc2xinput is already running.", consoleMode);

        var configIndex = Array.IndexOf(args, "--config");
        var configPath = Path.GetFullPath(configIndex >= 0 && configIndex + 1 < args.Length ? args[configIndex + 1] : Config.DefaultPath);
        Config config;
        try
        {
            config = Config.Load(configPath);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to load config {configPath}: {ex.Message}", consoleMode);
        }

        ViGEmClient client;
        try
        {
            client = new ViGEmClient();
        }
        catch (VigemBusNotFoundException)
        {
            return Fail("ViGEmBus driver not found. Install it with:\n\n    winget install ViGEm.ViGEmBus", consoleMode);
        }

        using (client)
        {
            if (consoleMode) return RunConsole(client, config, configPath);

            Log.ToFile(Path.Combine(Path.GetDirectoryName(configPath)!, "sc2xinput.log"));
            Log.Info($"Config: {configPath}");
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var tray = new TrayApp(client, config, configPath);
            Application.Run(tray);
            return 0;
        }
    }

    private static int RunConsole(ViGEmClient client, Config config, string configPath)
    {
        if (SteamIsRunning())
            Log.Warn("Steam is running. Steam also drives the Steam Controller and will fight over it " +
                     "(e.g. its desktop layout sending mouse/keyboard input). Quit Steam for best results.");
        Log.Info($"Config: {configPath}");
        Log.Info("Waiting for Steam Controllers... (Ctrl+C to quit)");

        var bridge = new Bridge(client, config);
        using var stop = new ManualResetEventSlim(false);
        // Ctrl+C lets the main thread clean up. For window close / logoff / shutdown, Windows terminates the
        // process as soon as the handler returns, so the handler has to clean up itself.
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Set(); });
        using var sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; bridge.Stop(); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; bridge.Stop(); });

        bridge.Start();
        stop.Wait();
        Log.Info("Shutting down, restoring controller mouse/keyboard mode...");
        bridge.Stop();
        return 0;
    }

    private static int Restore()
    {
        var found = 0;
        foreach (var device in Win32.GetHidDevicePaths().Select(ScDevice.TryOpen).OfType<ScDevice>())
        {
            using (device)
                if (device.SetLizardMode(true)) found++;
        }
        Log.Info(found > 0 ? "Restored mouse/keyboard mode." : "No Steam Controllers found.");
        return found > 0 ? 0 : 1;
    }

    private static int Fail(string message, bool consoleMode)
    {
        if (consoleMode)
            Log.Error(message);
        else
            MessageBox.Show(message, "sc2xinput", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }

    public static bool SteamIsRunning() => Process.GetProcessesByName("steam").Length > 0;
}
