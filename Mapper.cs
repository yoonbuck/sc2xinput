using Sc2XInput.SteamController;

namespace Sc2XInput;

internal readonly record struct XState(XButtons Buttons, byte LeftTrigger, byte RightTrigger,
    short LeftX, short LeftY, short RightX, short RightY);

internal static class Mapper
{
    private static readonly (ScButtons sc, XButtons x)[] DirectButtons =
    [
        (ScButtons.A, XButtons.A),
        (ScButtons.B, XButtons.B),
        (ScButtons.X, XButtons.X),
        (ScButtons.Y, XButtons.Y),
        (ScButtons.LB, XButtons.LeftShoulder),
        (ScButtons.RB, XButtons.RightShoulder),
        (ScButtons.Back, XButtons.Back),
        (ScButtons.Start, XButtons.Start),
        (ScButtons.StickClick, XButtons.LeftThumb),
    ];

    private static readonly double Sin22_5 = Math.Sin(Math.PI / 8);

    public static XState Map(ScInputState s, Config c)
    {
        var buttons = XButtons.None;
        foreach (var (sc, x) in DirectButtons)
            if (s.Has(sc)) buttons |= x;

        if (s.Has(ScButtons.Steam)) buttons |= c.SteamButton;
        if (s.Has(ScButtons.LGrip)) buttons |= c.LeftGrip;
        if (s.Has(ScButtons.RGrip)) buttons |= c.RightGrip;
        if (s.Has(ScButtons.RPadClick)) buttons |= c.RightPadClick;

        if (s.LPadTouched && (!c.LeftPadRequiresClick || s.Has(ScButtons.LPadClick)))
            buttons |= DPad(s.LPadX, s.LPadY, c.LeftPadDeadzone);

        var lt = s.Has(ScButtons.LTFull) ? (byte)255 : s.LeftTrigger;
        var rt = s.Has(ScButtons.RTFull) ? (byte)255 : s.RightTrigger;

        var (lx, ly) = Radial(s.StickX, s.StickY, c.StickDeadzone, 1.0);

        short rx = 0, ry = 0;
        if (c.RightPadMode == RightPadMode.Stick && s.RPadTouched)
            (rx, ry) = Radial(s.RPadX, s.RPadY, c.RightPadDeadzone, c.RightPadSensitivity);

        return new XState(buttons, lt, rt, lx, ly, rx, ry);
    }

    /// <summary>8-way direction: each cardinal owns a 45° wedge, diagonals fill the gaps.</summary>
    private static XButtons DPad(short x, short y, double deadzone)
    {
        var mag = Math.Sqrt((double)x * x + (double)y * y);
        if (mag < deadzone * short.MaxValue) return XButtons.None;

        var result = XButtons.None;
        if (y / mag > Sin22_5) result |= XButtons.DPadUp;
        if (y / mag < -Sin22_5) result |= XButtons.DPadDown;
        if (x / mag > Sin22_5) result |= XButtons.DPadRight;
        if (x / mag < -Sin22_5) result |= XButtons.DPadLeft;
        return result;
    }

    /// <summary>Applies a scaled radial deadzone and a gain, clamping to the unit circle.</summary>
    private static (short, short) Radial(short x, short y, double deadzone, double gain)
    {
        var nx = x / (double)short.MaxValue;
        var ny = y / (double)short.MaxValue;
        var mag = Math.Sqrt(nx * nx + ny * ny);
        if (mag <= deadzone || mag == 0) return (0, 0);

        var scaled = Math.Min(1.0, (mag - deadzone) / (1.0 - deadzone) * gain);
        var k = scaled / mag;
        return (ToShort(nx * k), ToShort(ny * k));
    }

    private static short ToShort(double v) => (short)Math.Clamp(Math.Round(v * short.MaxValue), -short.MaxValue, short.MaxValue);
}
