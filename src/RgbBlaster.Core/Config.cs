using System.Text.Json;
using System.Text.Json.Serialization;

namespace RgbBlaster.Core;

public sealed class AppConfig
{
    public int FrameRate { get; set; } = 60;
    public int CanvasWidth { get; set; } = 320;
    public int CanvasHeight { get; set; } = 200;
    public Fusion2Config Fusion2 { get; set; } = new();
    public List<ZonePlacement> Layout { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig()
            : new AppConfig();
}

public sealed class Fusion2Config
{
    public List<Fusion2HeaderConfig> Headers { get; set; } = [];

    /// <summary>
    /// Firmware zone numbers (0-10) driven as the "onboard" zone, one static color each. Empty uses the driver default.
    /// Zones 0-7 use commands 0x20-0x27, zones 8-10 use 0x90-0x92.
    /// </summary>
    public List<int> OnboardZones { get; set; } = [];

    /// <summary>
    /// Sends the lamp-array-off command (0x48 00) even when the board does not report support for it.
    /// The command takes the board out of Windows Dynamic Lighting control.
    /// </summary>
    public bool ForceLampArrayOff { get; set; }
}

public enum Fusion2Header { D_LED1, D_LED2, D_LED3, D_LED4, ONBOARD1, ONBOARD2 }

/// <summary>Wire color order. Auto uses the board's calibration value.</summary>
public enum ColorOrderSetting { Auto, RGB, GRB, BRG, RBG, GBR, BGR }

public sealed class Fusion2HeaderConfig
{
    public Fusion2Header Header { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Gigabyte headers do not detect strip length. 0 disables the header.</summary>
    public int LedCount { get; set; }
    public ColorOrderSetting ColorOrder { get; set; } = ColorOrderSetting.Auto;
}

public enum PlacementShape { Ring, Line }

/// <summary>Places a zone's LEDs on the canvas. Coordinates are 0..1 of canvas width/height.</summary>
public sealed class ZonePlacement
{
    public string Device { get; set; } = "";
    public string Zone { get; set; } = "";
    public PlacementShape Shape { get; set; }

    // Ring: one or more fans in a row on the same chain.
    public float X { get; set; }
    public float Y { get; set; }
    public float Radius { get; set; } = 0.05f;
    public int Fans { get; set; } = 1;
    /// <summary>Distance between fan centres for Fans &gt; 1.</summary>
    public float FanDx { get; set; }
    public float FanDy { get; set; }

    // Line.
    public float X2 { get; set; }
    public float Y2 { get; set; }
}
