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
    // Windows gives a console-close / logoff handler ~5 s in total; sessions close in parallel, waiting up
    // to this for their loops (plus up to ~1.2 s for device teardown).
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(2);

    private readonly ViGEmClient _client;
    private readonly Config _config;
    // Owned by the scan (and Stop), under this lock. Readers use _published instead, so the tray never
    // waits on a scan that is busy closing sessions.
    private readonly Dictionary<string, ControllerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private volatile KeyValuePair<string, ControllerSession>[] _published = [];
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase); // non-vendor interfaces (keyboard/mouse)
    private readonly HashSet<string> _startUncaptured; // consumed as each device is first seen
    private readonly Dictionary<string, int> _faults = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFaults = 3;
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
        var changed = false;
        lock (_sessions)
        {
            if (_stopped) return;
            var present = Win32.GetHidDevicePaths().Where(ScDevice.IsCandidatePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var doomed = _sessions.Where(s => s.Value.IsClosed || !present.Contains(s.Key)).ToList();
            foreach (var (path, session) in doomed)
            {
                _sessions.Remove(path);
                if (!session.Faulted) continue;
                // A replacement keeps the user's capture choice. A device that keeps faulting is left alone
                // until it's plugged in again, rather than flapping a virtual pad every scan.
                if (!session.Status.Captured) _startUncaptured.Add(path);
                if ((_faults[path] = _faults.GetValueOrDefault(path) + 1) >= MaxFaults)
                {
                    Log.Error($"[{session.Device.Name}] failed {MaxFaults} times; ignoring it until it is reconnected");
                    _ignored.Add(path);
                }
            }
            if (doomed.Count > 0)
            {
                _published = [.. _sessions];
                CloseAll(doomed.Select(s => s.Value).ToList(), StopBudget);
                changed = true;
            }
            _ignored.IntersectWith(present);
            foreach (var path in _faults.Keys.Where(p => !present.Contains(p)).ToList()) _faults.Remove(path);

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
                _published = [.. _sessions]; // before Start, so its first status change finds it listed
                session.Start();
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Closes sessions in parallel: each unplugs its pad, restores mouse mode and closes its device. Bounded
    /// so shutdown fits in the ~5 s Windows allows a console-close or logoff handler.
    /// </summary>
    private static void CloseAll(IReadOnlyCollection<ControllerSession> sessions, TimeSpan budget)
    {
        foreach (var session in sessions) session.RequestClose();
        var closing = sessions.Select(s => Task.Run(() => s.Dispose(budget))).ToArray();
        // Dispose waits up to the budget for the loop, then up to ~1.2 s for the haptic and reader threads.
        Task.WaitAll(closing, budget + TimeSpan.FromSeconds(1.5));
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
            CloseAll(_sessions.Values.ToList(), StopBudget);
            _sessions.Clear();
            _published = [];
        }
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(ScanInterval);
        Changed?.Invoke();
    }
}
