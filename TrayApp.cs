using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nefarius.ViGEm.Client;

namespace Sc2XInput;

/// <summary>Notification-area UI: status icon plus a menu to pause, configure and exit.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "sc2xinput";

    private readonly ViGEmClient _client;
    private readonly string _configPath;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripSeparator _statusSeparator;
    private readonly List<ToolStripItem> _statusItems = [];
    private readonly SynchronizationContext _ui;
    private Config _config;
    private Bridge? _bridge;
    private IntPtr _iconHandle;

    public TrayApp(ViGEmClient client, Config config, string configPath)
    {
        _client = client;
        _config = config;
        _configPath = configPath;

        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => SetEnabled(_bridge == null)) { Checked = true };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        _statusSeparator = new ToolStripSeparator();
        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _statusSeparator,
            _enabledItem,
            _startupItem,
            new ToolStripMenuItem("Edit config", null, (_, _) => OpenFile(_configPath)),
            new ToolStripMenuItem("Reload config", null, (_, _) => ReloadConfig()),
            new ToolStripMenuItem("Open log", null, (_, _) => { if (Log.FilePath != null) OpenFile(Log.FilePath); }),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit()),
        ]);
        _menu.Opening += (_, _) => RefreshMenu();

        // Creating the menu (a Control) installs the WinForms synchronization context.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // ContextMenuStrip only opens on right-click; show it for left-click too.
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(_tray, null);
        };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionEnded += OnSessionEnded;

        SetEnabled(true);

        if (Program.SteamIsRunning())
            _tray.ShowBalloonTip(5000, "Steam is running",
                "Steam also drives the Steam Controller and may fight over it. Quit Steam for best results.", ToolTipIcon.Warning);
    }

    /// <param name="uncaptured">Controllers to keep in mouse/keyboard mode (capture off) in the new bridge.</param>
    private void SetEnabled(bool enabled, IReadOnlySet<string>? uncaptured = null)
    {
        if (enabled && _bridge == null)
        {
            _bridge = new Bridge(_client, _config, uncaptured);
            _bridge.Changed += () => _ui.Post(_ => UpdateStatus(), null);
            _bridge.Start();
            Log.Info("Bridge enabled");
        }
        else if (!enabled && _bridge != null)
        {
            var bridge = _bridge;
            _bridge = null;
            bridge.Stop();
            Log.Info("Bridge paused, controller mouse/keyboard mode restored");
        }
        UpdateStatus();
    }

    private void ReloadConfig()
    {
        try
        {
            _config = Config.Load(_configPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load {_configPath}:\n\n{ex.Message}", "sc2xinput", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Log.Info("Config reloaded");
        if (_bridge == null) return;
        // Restart with the new config, but leave controllers the user handed back to mouse mode alone.
        var uncaptured = _bridge.UncapturedDevices;
        SetEnabled(false);
        SetEnabled(true, uncaptured);
    }

    private void UpdateStatus()
    {
        var controllers = _bridge?.Controllers ?? [];
        var state = _bridge == null ? IconState.Paused : controllers.Any(c => c.Captured) ? IconState.Active : IconState.Waiting;
        _tray.Text = state switch
        {
            IconState.Paused => "sc2xinput: paused",
            IconState.Waiting when controllers.Count == 0 => "sc2xinput: no controllers",
            _ => "sc2xinput: " + string.Join(", ", controllers.Select(ShortStatus)),
        };
        SetIcon(state);
        if (_menu.Visible) RefreshMenu();
    }

    private static string ShortStatus(ControllerInfo c) =>
        !c.Captured ? "mouse mode"
        : c.Player is { } p ? $"player {p + 1}, {c.RightPadMode.ToString().ToLowerInvariant()}"
        : "connecting";

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    private void RefreshMenu()
    {
        foreach (var item in _statusItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _statusItems.Clear();

        var controllers = _bridge?.Controllers ?? [];
        if (_bridge == null)
            _statusItems.Add(new ToolStripMenuItem("Paused") { Enabled = false });
        else if (controllers.Count == 0)
            _statusItems.Add(new ToolStripMenuItem("No controllers connected") { Enabled = false });
        else
            foreach (var c in controllers)
                _statusItems.Add(new ToolStripMenuItem($"{Capitalize(ShortStatus(c))}: {c.Name}") { Enabled = false });

        for (var i = 0; i < _statusItems.Count; i++) _menu.Items.Insert(i, _statusItems[i]);
        _enabledItem.Checked = _bridge != null;
        _startupItem.Checked = IsStartupEnabled();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string;
    }

    private static void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key.GetValue(RunValueName) is string)
            key.DeleteValue(RunValueName);
        else
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start("notepad.exe", $"\"{path}\""); // no app associated with the extension
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) UpdateStatus(); // light/dark taskbar switch
    }

    // Logoff/shutdown: Windows may terminate the process right after this returns, so restore synchronously.
    private void OnSessionEnded(object? sender, SessionEndedEventArgs e) => _bridge?.Stop();

    private void Exit()
    {
        _bridge?.Stop();
        _bridge = null;
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.SessionEnded -= OnSessionEnded;
            _tray.Dispose();
            _menu.Dispose();
            if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        }
        base.Dispose(disposing);
    }

    // ---- icon ----

    private enum IconState { Active, Waiting, Paused }

    private void SetIcon(IconState state)
    {
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
            DrawGlyph(g, size.Width, state);

        var handle = bmp.GetHicon();
        var old = _iconHandle;
        _tray.Icon = Icon.FromHandle(handle);
        _iconHandle = handle;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    /// <summary>Steam Controller silhouette: a body with two grips and two round trackpads cut out.</summary>
    private static void DrawGlyph(Graphics g, int px, IconState state)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(px / 32f, px / 32f); // draw in a 32x32 grid

        var baseColor = TaskbarIsLight() ? Color.Black : Color.White;
        var color = Color.FromArgb(state == IconState.Active ? 255 : 110, baseColor);

        using var body = RoundedRect(new RectangleF(2, 7, 28, 14), 6);
        using var leftGrip = new GraphicsPath();
        using var rightGrip = new GraphicsPath();
        leftGrip.AddEllipse(2, 12, 11, 15);
        rightGrip.AddEllipse(19, 12, 11, 15);
        using (var region = new Region(body))
        using (var brush = new SolidBrush(color))
        {
            region.Union(leftGrip);
            region.Union(rightGrip);
            g.FillRegion(brush, region);
        }

        g.CompositingMode = CompositingMode.SourceCopy;
        using (var hole = new SolidBrush(Color.Transparent))
        {
            g.FillEllipse(hole, 5, 9.5f, 8, 8);
            g.FillEllipse(hole, 19, 9.5f, 8, 8);
        }

        if (state == IconState.Paused)
        {
            g.CompositingMode = CompositingMode.SourceOver;
            using var pen = new Pen(baseColor, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 4, 4, 28, 28);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is 1;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
