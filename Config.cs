using System.Text.Json;
using System.Text.Json.Serialization;
using Sc2XInput.SteamController;

namespace Sc2XInput;

/// <summary>Xbox 360 (XUSB) button bits.</summary>
[Flags]
internal enum XButtons : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

internal enum RightPadMode
{
    /// <summary>Finger position on the pad is the stick position.</summary>
    Stick,
    /// <summary>Right pad does nothing (e.g. if you only want the stick).</summary>
    None,
    // Added last so the numeric values of the older modes don't change.
    /// <summary>Finger speed drives the stick, like Steam's Joystick Mouse: swipes turn the camera, a still finger stops it.</summary>
    Camera,
}

internal sealed class Config
{
    public XButtons LeftGrip { get; set; } = XButtons.LeftThumb;
    public XButtons RightGrip { get; set; } = XButtons.RightThumb;
    public XButtons SteamButton { get; set; } = XButtons.Guide;
    public XButtons RightPadClick { get; set; } = XButtons.RightThumb;

    /// <summary>Left pad acts as a D-pad only while clicked (like Steam's default) instead of on touch.</summary>
    public bool LeftPadRequiresClick { get; set; } = true;
    /// <summary>Fraction of the pad radius around the center that registers no direction.</summary>
    public double LeftPadDeadzone { get; set; } = 0.25;

    public RightPadMode RightPadMode { get; set; } = RightPadMode.Camera;

    /// <summary>Camera mode: stick deflection per unit of finger speed (pad radii per second).</summary>
    public double CameraSensitivity { get; set; } = 0.27;
    /// <summary>Camera mode: deflection that any real movement starts at, to get past the game's stick deadzone.</summary>
    public double CameraAntiDeadzone { get; set; } = 0.2;
    /// <summary>Camera mode: finger speed (pad radii per second) below which movement is ignored as jitter.</summary>
    public double CameraJitterThreshold { get; set; } = 0.15;
    /// <summary>Camera mode: window over which finger speed is averaged. Higher is smoother but laggier.</summary>
    public double CameraSmoothingMs { get; set; } = 30;
    /// <summary>Camera mode: keep turning after a flick and coast to a stop, like a trackball.</summary>
    public bool CameraTrackball { get; set; } = false;
    /// <summary>Camera mode: how quickly trackball motion slows down (per second; higher stops sooner).</summary>
    public double CameraTrackballFriction { get; set; } = 5;

    /// <summary>Stick mode: fraction of the pad radius around the center that registers no deflection.</summary>
    public double RightPadDeadzone { get; set; } = 0.05;
    /// <summary>Stick mode: multiplier applied after the deadzone; >1 lets you reach full deflection before the pad's edge.</summary>
    public double RightPadSensitivity { get; set; } = 1.3;

    /// <summary>Radial deadzone for the physical stick. Most games apply their own, so default is 0.</summary>
    public double StickDeadzone { get; set; } = 0.0;

    public bool Rumble { get; set; } = true;
    public double RumbleStrength { get; set; } = 1.0;

    /// <summary>
    /// Steam + button combinations. Keys are Steam Controller buttons (A, B, X, Y, LB, RB, Back, Start,
    /// LGrip, RGrip, LPadClick, RPadClick, StickClick). Empty disables chords, so Steam presses Guide immediately.
    /// </summary>
    public Dictionary<ScButtons, ChordAction> SteamChords { get; set; } = new()
    {
        // X, Y and the right pad click send nothing in lizard mode, so pressing them to recapture doesn't
        // leak a keypress (Back would type Tab).
        [ScButtons.Y] = ChordAction.ToggleCapture,
        [ScButtons.RPadClick] = ChordAction.CycleRightPadMode,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "sc2xinput.json");

    /// <summary>Loads the config, writing a default file on first run so the options are discoverable.</summary>
    public static Config Load(string path)
    {
        if (!File.Exists(path))
        {
            var config = new Config();
            try { File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return config;
        }
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOptions) ?? new Config();
    }
}
