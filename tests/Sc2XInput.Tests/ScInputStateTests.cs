using Sc2XInput.SteamController;
using B = Sc2XInput.SteamController.ScButtons;

namespace Sc2XInput.Tests;

public class ScInputStateTests
{
    /// <summary>A 64-byte input report payload (report-ID byte already stripped).</summary>
    private static byte[] Report(uint buttons, byte lt = 0, byte rt = 0, short x = 0, short y = 0, short rx = 0, short ry = 0)
    {
        var d = new byte[64];
        d[2] = ScProtocol.EvInput;
        d[8] = (byte)buttons; d[9] = (byte)(buttons >> 8); d[10] = (byte)(buttons >> 16);
        d[11] = lt; d[12] = rt;
        BitConverter.TryWriteBytes(d.AsSpan(16), x);
        BitConverter.TryWriteBytes(d.AsSpan(18), y);
        BitConverter.TryWriteBytes(d.AsSpan(20), rx);
        BitConverter.TryWriteBytes(d.AsSpan(22), ry);
        return d;
    }

    [Fact]
    public void CapturedReport_RightPadTouch()
    {
        // Bytes 8-23 of a real report from the wireless dongle: thumb resting on the right pad.
        var d = new byte[64];
        d[2] = ScProtocol.EvInput;
        Convert.FromHexString("000010" + "0000" + "000000" + "000000007EB5B4BE").CopyTo(d, 8);
        var s = new ScInputState();
        s.Apply(d);
        Assert.Equal(B.RPadTouch, s.Buttons);
        Assert.True(s.RPadTouched);
        Assert.Equal((short)-19074, s.RPadX);
        Assert.Equal((short)-16716, s.RPadY);
    }

    [Fact]
    public void ButtonsAndTriggers()
    {
        var s = new ScInputState();
        s.Apply(Report((uint)(B.A | B.LB | B.Steam | B.RTFull), lt: 100, rt: 255));
        Assert.Equal(B.A | B.LB | B.Steam | B.RTFull, s.Buttons);
        Assert.Equal(100, s.LeftTrigger);
        Assert.Equal(255, s.RightTrigger);
    }

    [Fact]
    public void SharedField_IsTheStick_UnlessTheLeftPadIsTouched()
    {
        var s = new ScInputState();
        s.Apply(Report(0, x: 1000, y: -2000));
        Assert.Equal((1000, -2000), (s.StickX, s.StickY));
        Assert.False(s.LPadTouched);

        s.Apply(Report((uint)B.LPadTouch, x: 300, y: 400));
        Assert.Equal((300, 400), (s.LPadX, s.LPadY));
        Assert.Equal((0, 0), (s.StickX, s.StickY)); // only the pad is in use
        Assert.True(s.LPadTouched);
    }

    [Fact]
    public void StickAndLeftPadTogether_AlternateReports_KeepBoth()
    {
        var s = new ScInputState();
        s.Apply(Report((uint)(B.LPadAndStick | B.LPadTouch), x: 15000, y: -80)); // pad report
        s.Apply(Report((uint)B.LPadAndStick, x: 32767, y: 0));                   // stick report
        Assert.Equal((15000, -80), (s.LPadX, s.LPadY));
        Assert.Equal((32767, 0), (s.StickX, s.StickY));
        Assert.True(s.LPadTouched);
    }
}
