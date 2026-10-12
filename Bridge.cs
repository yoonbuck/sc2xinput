using Nefarius.ViGEm.Client;
using Sc2XInput.Native;
using Sc2XInput.SteamController;

namespace Sc2XInput;

internal readonly record struct ControllerInfo(string Name, int? Player, bool Captured, RightPadMode RightPadMode);

/// <summary>Watches for Steam Controllers and keeps one <see cref="ControllerSession"/> per device. Single use.</summary>
internal sealed class Bridge
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly ViGEmClient _client;
    private readonly Config _config;
    private readonly Dictionary<string, ControllerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase); // non-vendor interfaces (keyboard/mouse)
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private bool _stopped;

    /// <summary>Raised from background threads whenever a controller connects, disconnects or gets a player slot.</summary>
    public event Action? Changed;

    private readonly HashSet<string> _startUncaptured; // consumed as each device is first seen

    /// <param name="uncaptured">Device paths to start with capture off, see <see cref="UncapturedDevices"/>.</param>
    public Bridge(ViGEmClient client, Config config, IReadOnlySet<string>? uncaptured = null)
    {
        _client = client;
        _config = config;
        _startUncaptured = new HashSet<string>(uncaptured ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Device paths of controllers whose capture is off (connected or asleep), to carry over to a new bridge.</summary>
    public IReadOnlySet<string> UncapturedDevices
    {
        get
        {
            lock (_sessions)
                return _sessions.Where(s => !s.Value.IsClosed && !s.Value.IsCaptured) // including asleep ones
                    .Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<ControllerInfo> Controllers
    {
        get
        {
            lock (_sessions)
                return _sessions.Values.Where(s => s.HasController)
                    .Select(s => new ControllerInfo(s.Device.Name, s.PlayerIndex, s.IsCaptured, s.RightPadMode))
                    .OrderBy(c => c.Player ?? int.MaxValue).ToList();
        }
    }

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
            foreach (var session in _sessions.Values) session.Dispose();
            _sessions.Clear();
        }
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(ScanInterval);
        Changed?.Invoke();
    }
}
