using System.Runtime.InteropServices;
using Sc2XInput.SteamController;

namespace Sc2XInput.DevTools;

/// <summary>
/// One timeline of Steam Controller button edges (read from its vendor HID interface) and the keyboard keys,
/// mouse buttons and wheel that Windows receives from the controller's own keyboard/mouse interfaces.
/// </summary>
internal static class InputLog
{
    // Bits that ride along with the buttons but aren't presses (touch, stick/pad status, firmware D-pad zones).
    private const ScButtons NotButtons = ScButtons.LPadTouch | ScButtons.RPadTouch | ScButtons.LPadAndStick
        | ScButtons.LPadUp | ScButtons.LPadDown | ScButtons.LPadLeft | ScButtons.LPadRight;

    public static int Run(double seconds)
    {
        var gate = new object();
        var previous = ScButtons.None;
        var devices = Program.OpenControllers((_, r) =>
        {
            var buttons = (ScButtons)(r[8] | (r[9] << 8) | (r[10] << 16)) & ~NotButtons;
            lock (gate)
            {
                if (buttons == previous) return;
                if ((buttons & ~previous) is var down and not 0) Console.WriteLine($"{Program.Now()} BUTTON {down} down");
                if ((previous & ~buttons) is var up and not 0) Console.WriteLine($"{Program.Now()} BUTTON {up} up");
                previous = buttons;
            }
        });

        using var sink = new RawInputSink(line => { lock (gate) Console.WriteLine($"{Program.Now()} {line}"); });
        var timer = new System.Windows.Forms.Timer { Interval = (int)(seconds * 1000) };
        timer.Tick += (_, _) => Application.ExitThread();
        timer.Start();
        Application.Run();
        devices.ForEach(d => d.Dispose());
        return 0;
    }

    /// <summary>Message-only window registered for raw keyboard and mouse input from all devices.</summary>
    private sealed unsafe class RawInputSink : NativeWindow, IDisposable
    {
        private const int WM_INPUT = 0xFF;
        private const uint RIDEV_INPUTSINK = 0x100, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
        private const uint RIM_TYPEKEYBOARD = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr input, uint command, byte* data, ref uint size, uint headerSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, char* data, ref uint size);

        private static readonly (ushort flag, string name)[] MouseButtons =
        [
            (0x001, "left down"), (0x002, "left up"), (0x004, "right down"), (0x008, "right up"),
            (0x010, "middle down"), (0x020, "middle up"), (0x040, "x1 down"), (0x080, "x1 up"),
            (0x100, "x2 down"), (0x200, "x2 up"), (0x400, "wheel"), (0x800, "hwheel"),
        ];

        private readonly Action<string> _log;
        private readonly Dictionary<IntPtr, bool> _isController = [];

        public RawInputSink(Action<string> log)
        {
            _log = log;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
            RawInputDevice[] devices =
            [
                new() { UsagePage = 1, Usage = 6, Flags = RIDEV_INPUTSINK, Target = Handle }, // keyboard
                new() { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK, Target = Handle }, // mouse
            ];
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)sizeof(RawInputDevice)))
                throw new System.ComponentModel.Win32Exception();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) OnRawInput(m.LParam);
            base.WndProc(ref m);
        }

        private void OnRawInput(IntPtr input)
        {
            var header = (uint)(8 + 2 * IntPtr.Size); // RAWINPUTHEADER
            uint size = 0;
            GetRawInputData(input, RID_INPUT, null, ref size, header);
            var buffer = stackalloc byte[(int)size];
            if (GetRawInputData(input, RID_INPUT, buffer, ref size, header) != size) return;

            var type = *(uint*)buffer;
            var device = *(IntPtr*)(buffer + 8);
            if (!IsController(device)) return;
            var body = buffer + header;

            if (type == RIM_TYPEKEYBOARD)
            {
                var flags = *(ushort*)(body + 2);
                var vkey = *(ushort*)(body + 6);
                _log($"KEY {(Keys)vkey} (0x{vkey:X2}) {((flags & 1) != 0 ? "up" : "down")}");
            }
            else
            {
                var flags = *(ushort*)(body + 4);
                if (flags == 0) return; // plain movement
                var wheel = (short)*(ushort*)(body + 6);
                var names = MouseButtons.Where(b => (flags & b.flag) != 0).Select(b => b.flag is 0x400 or 0x800 ? $"{b.name} {wheel}" : b.name);
                _log($"MOUSE {string.Join(", ", names)}");
            }
        }

        private bool IsController(IntPtr device)
        {
            if (_isController.TryGetValue(device, out var known)) return known;
            uint size = 512;
            var name = stackalloc char[512];
            var result = GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, name, ref size);
            var path = result is > 0 and not uint.MaxValue ? new string(name) : ""; // uint.MaxValue = error
            return _isController[device] = path.Contains("VID_28DE", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose() => DestroyHandle();
    }
}
