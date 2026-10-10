using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sc2XInput.Native;

internal static unsafe class Win32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_OPERATION_ABORTED = 995;

    public const ushort HID_USAGE_PAGE_VENDOR = 0xFF00;

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, byte* buffer, int length, out int read, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle h, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    /// <summary>
    /// This is a GUI-subsystem exe, so it has no console by default. Command-line modes attach to the
    /// terminal they were started from, or open a new console when started from Explorer.
    /// Does nothing if stdout is already redirected (e.g. piped to a file).
    /// </summary>
    public static void EnsureConsole()
    {
        if (Console.IsOutputRedirected) return;
        const int ATTACH_PARENT_PROCESS = -1;
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
    }

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

    [DllImport("hid.dll")]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, char* buffer, int lengthBytes);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static extern int CM_Get_Device_Interface_List_Size(out int length, ref Guid classGuid, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static extern int CM_Get_Device_Interface_List(ref Guid classGuid, string? deviceId, char[] buffer, int length, int flags);

    private const int CR_SUCCESS = 0;
    private const int CR_BUFFER_SMALL = 0x1A;

    /// <summary>Returns device interface paths for all present HID devices.</summary>
    public static List<string> GetHidDevicePaths()
    {
        HidD_GetHidGuid(out var guid);
        while (true)
        {
            if (CM_Get_Device_Interface_List_Size(out var len, ref guid, null, 0) != CR_SUCCESS)
                return [];
            var buffer = new char[len];
            var cr = CM_Get_Device_Interface_List(ref guid, null, buffer, len, 0);
            if (cr == CR_BUFFER_SMALL) continue; // a device arrived between the two calls
            if (cr != CR_SUCCESS) return [];
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }

    public static bool TryGetCaps(SafeFileHandle h, out HIDP_CAPS caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(h, out var preparsed)) return false;
        try
        {
            const int HIDP_STATUS_SUCCESS = 0x00110000;
            return HidP_GetCaps(preparsed, out caps) == HIDP_STATUS_SUCCESS;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    public static string? GetSerial(SafeFileHandle h)
    {
        var buf = stackalloc char[128];
        if (!HidD_GetSerialNumberString(h, buf, 256)) return null;
        var s = new string(buf);
        return s.Length == 0 ? null : s;
    }
}
