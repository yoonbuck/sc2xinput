using Nefarius.ViGEm.Client;
using Sc2XInput.Native;
using Sc2XInput.SteamController;

namespace Sc2XInput;

internal readonly record struct ControllerInfo(string Name, int? Player);

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

    public Bridge(ViGEmClient client, Config config)
    {
        _client = client;
        _config = config;
    }

    public IReadOnlyList<ControllerInfo> Controllers
    {
        get
        {
            lock (_sessions)
                return _sessions.Values.Where(s => s.HasController)
                    .Select(s => new ControllerInfo(s.Device.Name, s.PlayerIndex))
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
                var session = new ControllerSession(device, _client, _config);
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
