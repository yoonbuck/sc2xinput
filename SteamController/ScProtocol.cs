using System.Buffers.Binary;

namespace Sc2XInput.SteamController;

// Protocol notes are from the Linux hid-steam driver and SDL's HIDAPI Steam driver.
// Offsets are into the 64-byte vendor report, *without* the leading HID report-ID byte
// that Windows prepends.

internal static class ScProtocol
{
    public const ushort VendorId = 0x28DE;
    public const ushort WiredPid = 0x1102;
    public const ushort DonglePid = 0x1142;

    // Report types (byte 2)
    public const byte EvInput = 0x01;
    public const byte EvConnection = 0x03;
    public const byte EvBattery = 0x04;

    // Connection event payload (byte 4)
    public const byte ConnDisconnected = 0x01;
    public const byte ConnConnected = 0x02;

    // Feature-report commands
    public const byte CmdClearMappings = 0x81;
    public const byte CmdDefaultMappings = 0x85;
    public const byte CmdWriteRegister = 0x87;
    public const byte CmdDefaultMouse = 0x8E;
    public const byte CmdHapticPulse = 0x8F;
    public const byte CmdRequestCommStatus = 0xB4;

    // Registers
    public const byte RegRPadMode = 0x08;
    public const byte RegRPadMargin = 0x18;
    public const byte RegGyroMode = 0x30;
}

[Flags]
internal enum ScButtons : uint
{
    None = 0,
    RTFull = 1 << 0,
    LTFull = 1 << 1,
    RB = 1 << 2,
    LB = 1 << 3,
    Y = 1 << 4,
    B = 1 << 5,
    X = 1 << 6,
    A = 1 << 7,
    LPadUp = 1 << 8, // firmware's own d-pad zones, only set while the left pad is clicked
    LPadRight = 1 << 9,
    LPadLeft = 1 << 10,
    LPadDown = 1 << 11,
    Back = 1 << 12,
    Steam = 1 << 13,
    Start = 1 << 14,
    LGrip = 1 << 15,
    RGrip = 1 << 16,
    LPadClick = 1 << 17,
    RPadClick = 1 << 18,
    LPadTouch = 1 << 19, // byte 16-19 hold left-pad coords in this report (not stick)
    RPadTouch = 1 << 20,
    StickClick = 1 << 22,
    LPadAndStick = 1 << 23, // stick and left pad are both in use; reports alternate between them
}

/// <summary>
/// Accumulated controller state. The stick and the left pad share a field in the report,
/// so state has to persist across reports.
/// </summary>
internal sealed class ScInputState
{
    public ScButtons Buttons;
    public byte LeftTrigger, RightTrigger;
    public short StickX, StickY;
    public short LPadX, LPadY;
    public short RPadX, RPadY;
    public bool LPadTouched;
    public bool RPadTouched;

    public bool Has(ScButtons b) => (Buttons & b) != 0;

    public void Apply(ReadOnlySpan<byte> d)
    {
        Buttons = (ScButtons)(d[8] | (d[9] << 8) | (d[10] << 16));
        LeftTrigger = d[11];
        RightTrigger = d[12];

        var x = BinaryPrimitives.ReadInt16LittleEndian(d[16..]);
        var y = BinaryPrimitives.ReadInt16LittleEndian(d[18..]);
        var both = Has(ScButtons.LPadAndStick);

        if (Has(ScButtons.LPadTouch))
        {
            LPadX = x; LPadY = y;
            if (!both) { StickX = 0; StickY = 0; }
        }
        else
        {
            StickX = x; StickY = y;
            if (!both) { LPadX = 0; LPadY = 0; }
        }
        LPadTouched = Has(ScButtons.LPadTouch) || both;

        RPadTouched = Has(ScButtons.RPadTouch);
        RPadX = BinaryPrimitives.ReadInt16LittleEndian(d[20..]);
        RPadY = BinaryPrimitives.ReadInt16LittleEndian(d[22..]);
    }
}
