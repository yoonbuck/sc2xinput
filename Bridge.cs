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
    // Windows gives a console-close / logoff handler ~5 s in total. Stop fits everything (waiting for a scan
    // in progress, the session loops, device teardown) into one deadline under that.
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(4.5);
    // Per session, after its loop has finished: up to 0.2 s for the haptic worker, 1 s for the HID reader
    // and 0.5 s for the control lock (see ControllerSession.Dispose and ScDevice.Dispose).
    private static readonly TimeSpan TeardownAllowance = TimeSpan.FromSeconds(1.7);
    // Sessions a scan replaces get a short close, so one running at shutdown leaves Stop most of its budget.
    private static readonly TimeSpan ScanCloseBudget = TimeSpan.FromSeconds(1);

    private readonly ViGEmClient _client;
    private readonly Config _config;
    // Owned by the scan (and Stop), under this lock. Readers use _published instead, so the tray never
    // waits on a scan that is busy closing sessions.
    private readonly Dictionary<string, ControllerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private volatile KeyValuePair<string, ControllerSession>[] _published = [];
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase); // non-vendor interfaces (keyboard/mouse)
    private readonly HashSet<string> _startUncaptured; // consumed as each device is first seen
    private readonly Dictionary<string, int> _faults = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFaults = 3; // then a cooldown before the next attempt
    private readonly Dictionary<string, long> _quarantinedUntil = new(StringComparer.OrdinalIgnoreCase);
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
            var now = Stopwatch.GetTimestamp();
            foreach (var (path, session) in doomed)
            {
                _sessions.Remove(path);
                if (!session.Faulted) continue;
                // A replacement keeps the user's capture choice. A device that keeps faulting is left alone for
                // a cooldown that doubles each time (a dongle slot's path never goes away, so "until it's
                // reconnected" would mean until the dongle is unplugged), rather than flapping a pad every scan.
                if (!session.Status.Captured) _startUncaptured.Add(path);
                var faults = _faults[path] = _faults.GetValueOrDefault(path) + 1;
                if (faults >= MaxFaults)
                {
                    var cooldown = TimeSpan.FromMinutes(Math.Min(15, Math.Pow(2, faults - MaxFaults)));
                    Log.Error($"[{session.Device.Name}] failed {faults} times; retrying in {cooldown.TotalMinutes:0} min");
                    _quarantinedUntil[path] = now + (long)(cooldown.TotalSeconds * Stopwatch.Frequency);
                }
            }
            if (doomed.Count > 0)
            {
                _published = [.. _sessions];
                CloseAll(doomed.Select(s => s.Value).ToList(), ScanCloseBudget);
                changed = true;
            }
            _ignored.IntersectWith(present);
            foreach (var path in _faults.Keys.Where(p => !present.Contains(p)).ToList())
            {
                _faults.Remove(path); // re-plugged: start over
                _quarantinedUntil.Remove(path);
            }

            foreach (var path in present)
            {
                if (_sessions.ContainsKey(path) || _ignored.Contains(path)) continue;
                if (_quarantinedUntil.TryGetValue(path, out var until) && now < until) continue;
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
    /// Closes sessions in parallel: each unplugs its pad, restores mouse mode and closes its device. Waits up
    /// to <paramref name="loopBudget"/> for their loops plus <see cref="TeardownAllowance"/>.
    /// </summary>
    private static void CloseAll(IReadOnlyCollection<ControllerSession> sessions, TimeSpan loopBudget)
    {
        foreach (var session in sessions) session.RequestClose();
        var closing = sessions.Select(s => Task.Run(() => s.Dispose(loopBudget))).ToArray();
        Task.WaitAll(closing, loopBudget + TeardownAllowance);
    }

    /// <summary>
    /// Disconnects the virtual pads and restores the controllers' mouse/keyboard mode. Synchronous and bounded
    /// by one deadline, so it is safe to call from handlers after which Windows terminates the process
    /// (window close, logoff).
    /// </summary>
    public void Stop()
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(StopBudget.TotalSeconds * Stopwatch.Frequency);
        TimeSpan Left() => TimeSpan.FromSeconds(Math.Max(0, deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);

        _stop.Set();
        // A scan may hold the lock while it closes sessions it is replacing; wait for it within the deadline.
        if (!Monitor.TryEnter(_sessions, Left()))
        {
            Log.Warn("shutdown timed out waiting for the device scan; run sc2xinput --restore if mouse mode is off");
            return;
        }
        try
        {
            if (_stopped) return;
            _stopped = true;
            var loopBudget = Left() - TeardownAllowance;
            CloseAll(_sessions.Values.ToList(), loopBudget > TimeSpan.Zero ? loopBudget : TimeSpan.Zero);
            _sessions.Clear();
            _published = [];
        }
        finally
        {
            Monitor.Exit(_sessions);
        }
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(Left());
        Changed?.Invoke();
    }
}
