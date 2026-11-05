using System.Text.Json;
using System.Text.Json.Serialization;

namespace RgbBlaster.Core;

public sealed class AppConfig
{
    public int FrameRate { get; set; } = 30;
    public int CanvasWidth { get; set; } = 320;
    public int CanvasHeight { get; set; } = 200;
    public Fusion2Config Fusion2 { get; set; } = new();
    public List<ZonePlacement> Layout { get; set; } = [];
    public ExitLighting OnExit { get; set; } = ExitLighting.SaveLast;
    /// <summary>Motherboard fan headers the app may control. Headers not listed stay on the BIOS curve.</summary>
    public List<BoardFanConfig> BoardFans { get; set; } = [];
    /// <summary>Temperature-based fan curves applied while "run" is active. Fans not in any curve keep their default duty.</summary>
    public List<FanCurveConfig> FanCurves { get; set; } = [];
    /// <summary>Web GUI and API of the service.</summary>
    public WebConfig Web { get; set; } = new();
    /// <summary>Sound levels sent by the tray app.</summary>
    public AudioConfig Audio { get; set; } = new();

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

public sealed class WebConfig
{
    public int Port { get; set; } = 27830;
    /// <summary>False binds to 127.0.0.1 only. True binds to all interfaces; the API has no authentication.</summary>
    public bool AllowLan { get; set; }
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

public sealed class BoardFanConfig
{
    /// <summary>Super I/O fan channel, 0-based, as listed by <c>board-fans</c>.</summary>
    public int Channel { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lowest duty the app sets. Requests below it are raised to it, so the fans never stall.</summary>
    public byte MinDutyPercent { get; set; } = 30;
}

public sealed class FanCurveConfig
{
    public string Name { get; set; } = "";
    /// <summary>Sensors read each second; the curve uses the hottest. See HardwareMonitorSession.ReadCelsius for names.</summary>
    public List<string> Sensors { get; set; } = ["cpu"];
    /// <summary>Temperature to duty points, interpolated linearly and held flat beyond the ends.</summary>
    public List<FanCurvePoint> Points { get; set; } = [];
    /// <summary>Fan channel ids, as listed at "run" start (for example board1, ch13).</summary>
    public List<string> Fans { get; set; } = [];
    /// <summary>
    /// The curve uses the mean temperature of this many past seconds, so short spikes (Ryzen Tctl jumps 5-12 C on brief loads)
    /// do not change fan speed. 0 or less uses the latest reading only.
    /// </summary>
    public float AverageSeconds { get; set; } = 10;
    /// <summary>Largest duty rise per second. Rising is fast so a load spike is met quickly.</summary>
    public float RisePercentPerSecond { get; set; } = 10;
    /// <summary>Largest duty fall per second. Falling is slow so short load dips do not make the fans hunt.</summary>
    public float FallPercentPerSecond { get; set; } = 2;
    /// <summary>Duty used while no sensor gives a reading.</summary>
    public byte FailsafePercent { get; set; } = 100;
}

public sealed class FanCurvePoint
{
    public float Temp { get; set; }
    public byte Duty { get; set; }
}

/// <summary>What the lights show after the app exits.</summary>
public enum ExitLighting
{
    /// <summary>Devices run their stored firmware effects.</summary>
    Firmware,
    /// <summary>Everything is set black. Devices may return to their stored effects after their own timeout.</summary>
    Off,
    /// <summary>Devices that can store lighting keep the last colors. Others hold them until their own timeout.</summary>
    SaveLast,
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

/// <summary>Grid places each LED at the device-reported position inside the rectangle X,Y to X2,Y2.</summary>
public enum PlacementShape { Ring, Line, Grid }

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
    /// <summary>Distance between fan centers for Fans &gt; 1.</summary>
    public float FanDx { get; set; }
    public float FanDy { get; set; }

    // Line and Grid (Grid: X,Y is the top-left corner, X2,Y2 the bottom-right).
    public float X2 { get; set; }
    public float Y2 { get; set; }

    /// <summary>Degrees, clockwise. Ring: where LED 1 sits, 0 = 12 o'clock. Grid: turns the grid about its center.</summary>
    public float Rotation { get; set; }
    /// <summary>Ring: LEDs run counter-clockwise. Line: LED 1 is at the X2,Y2 end.</summary>
    public bool Reverse { get; set; }
}
