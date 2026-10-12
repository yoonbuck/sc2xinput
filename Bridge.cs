using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Sc2XInput.Native;
using Sc2XInput.SteamController;

namespace Sc2XInput;

internal readonly record struct ControllerInfo(string Name, int? Player, bool Captured, RightPadMode RightPadMode);

/// <summary>Watches for Steam Controllers and keeps one <see cref="ControllerSession"/> per device. Single use.</summary>
internal sealed class Bridge
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    // Windows gives a console-close / logoff handler ~5 s in total; sessions close in parallel within this.
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(2);

    private readonly ViGEmClient _client;
    private readonly Config _config;
    // Owned by the scan (and Stop), under this lock. Readers use _published instead, so the tray never
    // waits on a scan that is busy closing sessions.
    private readonly Dictionary<string, ControllerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private volatile KeyValuePair<string, ControllerSession>[] _published = [];
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase); // non-vendor interfaces (keyboard/mouse)
    private readonly HashSet<string> _startUncaptured; // consumed as each device is first seen
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private bool _stopped;

    /// <summary>
    /// Raised from background threads whenever a controller's status changes. Handlers must not block on the
    /// Bridge (the tray posts to its UI thread).
    /// </summary>
    public event Action? Changed;

    /// <param name="uncaptured">Device paths to start with capture off, see <see cref="UncapturedDevices"/>.</param>
    public Bridge(ViGEmClient client, Config config, IReadOnlySet<string>? uncaptured = null)
    {
        _client = client;
        _config = config;
        _startUncaptured = new HashSet<string>(uncaptured ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Device paths of controllers whose capture is off (connected or asleep), to carry over to a new bridge.</summary>
    public IReadOnlySet<string> UncapturedDevices =>
        _published.Where(s => s.Value.Status is { IsClosed: false, Captured: false })
            .Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ControllerInfo> Controllers =>
        _published.Select(s => (s.Value.Device.Name, Status: s.Value.Status))
            .Where(s => s.Status.Present)
            .Select(s => new ControllerInfo(s.Name, s.Status.Player, s.Status.Captured, s.Status.Mode))
            .OrderBy(c => c.Player ?? int.MaxValue).ToList();

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "device scan" };
        _thread.Start();
    }

    private void Run()
    {
        do Scan();
        while (!_stop.Wait(ScanInterval));
    }

    private void Scan()
    {
        lock (_sessions)
        {
            if (_stopped) return;
            var present = Win32.GetHidDevicePaths().Where(ScDevice.IsCandidatePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (path, session) in _sessions.ToList())
            {
                if (session.IsClosed || !present.Contains(path))
                {
                    session.Dispose();
                    _sessions.Remove(path);
                }
            }
            _ignored.IntersectWith(present);

            foreach (var path in present)
            {
                if (_sessions.ContainsKey(path) || _ignored.Contains(path)) continue;
                var device = ScDevice.TryOpen(path);
                if (device == null)
                {
                    _ignored.Add(path);
                    continue;
                }
                Log.Info($"[{device.Name}] found");
                // Only the first session for a path inherits it: if the device re-enumerates later, it starts
                // captured like any newly plugged-in controller.
                var session = new ControllerSession(device, _client, _config, captured: !_startUncaptured.Remove(path));
                session.Changed += () => Changed?.Invoke();
                _sessions[path] = session;
                session.Start();
            }
            _published = [.. _sessions];
        }
    }

    /// <summary>
    /// Disconnects the virtual pads and restores the controllers' mouse/keyboard mode. Synchronous, so it
    /// is safe to call from handlers after which Windows terminates the process (window close, logoff).
    /// </summary>
    public void Stop()
    {
        _stop.Set();
        lock (_sessions)
        {
            if (_stopped) return;
            _stopped = true;
            // Close every session at once, then wait for them all against one deadline.
            foreach (var session in _sessions.Values) session.RequestClose();
            var deadline = Stopwatch.GetTimestamp() + (long)(StopBudget.TotalSeconds * Stopwatch.Frequency);
            foreach (var session in _sessions.Values)
            {
                var left = TimeSpan.FromSeconds(Math.Max(0, deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                session.Dispose(left);
            }
            _sessions.Clear();
            _published = [];
        }
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(ScanInterval);
        Changed?.Invoke();
    }
}
