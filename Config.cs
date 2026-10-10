using System.Text.Json;
using System.Text.Json.Serialization;

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

    public RightPadMode RightPadMode { get; set; } = RightPadMode.Stick;
    public double RightPadDeadzone { get; set; } = 0.05;
    /// <summary>Multiplier applied after the deadzone; >1 lets you reach full deflection before the pad's edge.</summary>
    public double RightPadSensitivity { get; set; } = 1.3;

    /// <summary>Radial deadzone for the physical stick. Most games apply their own, so default is 0.</summary>
    public double StickDeadzone { get; set; } = 0.0;

    public bool Rumble { get; set; } = true;
    public double RumbleStrength { get; set; } = 1.0;

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
