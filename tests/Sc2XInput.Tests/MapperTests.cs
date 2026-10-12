using Sc2XInput.SteamController;
using B = Sc2XInput.SteamController.ScButtons;

namespace Sc2XInput.Tests;

public class MapperTests
{
    private static XState Map(ScInputState s, Config? c = null, RightPadMode? mode = null)
    {
        c ??= new Config();
        return Mapper.Map(s, s.Buttons, c, mode ?? c.RightPadMode);
    }

    [Fact]
    public void FaceButtons_Bumpers_Menu_Map_OneToOne()
    {
        var x = Map(new ScInputState { Buttons = B.A | B.B | B.X | B.Y | B.LB | B.RB | B.Back | B.Start | B.StickClick });
        Assert.Equal(XButtons.A | XButtons.B | XButtons.X | XButtons.Y | XButtons.LeftShoulder | XButtons.RightShoulder
                     | XButtons.Back | XButtons.Start | XButtons.LeftThumb, x.Buttons);
    }

    [Fact]
    public void Steam_Grips_RightPadClick_FollowConfig()
    {
        var c = new Config { SteamButton = XButtons.Guide, LeftGrip = XButtons.A, RightGrip = XButtons.B, RightPadClick = XButtons.None };
        var x = Map(new ScInputState { Buttons = B.Steam | B.LGrip | B.RGrip | B.RPadClick }, c);
        Assert.Equal(XButtons.Guide | XButtons.A | XButtons.B, x.Buttons);
    }

    [Fact]
    public void TriggerFullClick_Is255()
    {
        var x = Map(new ScInputState { Buttons = B.LTFull, LeftTrigger = 200, RightTrigger = 40 });
        Assert.Equal(255, x.LeftTrigger);
        Assert.Equal(40, x.RightTrigger);
    }

    [Fact]
    public void LeftPad_IsADPad_OnlyWhileClicked_ByDefault()
    {
        var touched = new ScInputState { Buttons = B.LPadTouch, LPadTouched = true, LPadX = 0, LPadY = 30000 };
        Assert.Equal(XButtons.None, Map(touched).Buttons);

        var clicked = new ScInputState { Buttons = B.LPadTouch | B.LPadClick, LPadTouched = true, LPadX = 0, LPadY = 30000 };
        Assert.Equal(XButtons.DPadUp, Map(clicked).Buttons);

        var diagonal = new ScInputState { Buttons = B.LPadClick, LPadTouched = true, LPadX = -20000, LPadY = -20000 };
        Assert.Equal(XButtons.DPadDown | XButtons.DPadLeft, Map(diagonal).Buttons);
    }

    [Fact]
    public void StickClick_WithoutPadTouch_IsNotADPadPress()
    {
        // The firmware sets the left-pad click bit along with the stick click.
        var x = Map(new ScInputState { Buttons = B.StickClick | B.LPadClick, LPadX = 0, LPadY = 30000 });
        Assert.Equal(XButtons.LeftThumb, x.Buttons);
    }

    [Fact]
    public void RightPad_StickMode_UsesPositionWithDeadzoneAndGain()
    {
        var s = new ScInputState { RPadTouched = true, RPadX = 1000, RPadY = 0 }; // inside the 5% deadzone
        Assert.Equal((0, 0), (Map(s, mode: RightPadMode.Stick).RightX, Map(s, mode: RightPadMode.Stick).RightY));

        s.RPadX = 26000; // past full deflection once the 1.3× gain applies
        Assert.Equal(short.MaxValue, Map(s, mode: RightPadMode.Stick).RightX);
    }

    [Theory]
    [InlineData(nameof(RightPadMode.Camera))]
    [InlineData(nameof(RightPadMode.None))]
    public void RightPad_NonStickModes_LeaveTheRightStickToOthers(string modeName)
    {
        var mode = Enum.Parse<RightPadMode>(modeName);
        var x = Map(new ScInputState { RPadTouched = true, RPadX = 20000, RPadY = 20000 }, mode: mode);
        Assert.Equal((0, 0), (x.RightX, x.RightY));
    }
}
