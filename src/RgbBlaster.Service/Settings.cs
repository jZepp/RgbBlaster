using System.Text.Json;
using RgbBlaster.Core;

namespace RgbBlaster.Service;

/// <summary>Everything the GUI can change. Hardware facts (headers, LED counts, board fans) stay in rgbblaster.json.</summary>
public sealed class RuntimeSettings
{
    public string ActiveProfile { get; set; } = "Default";
    public List<Profile> Profiles { get; set; } = [];
    /// <summary>Where each zone sits in the case. Shared by all profiles.</summary>
    public List<ZonePlacement> Layout { get; set; } = [];
    /// <summary>Lighting frames per second, 10..60. Null uses FrameRate from rgbblaster.json. Shared by all profiles.</summary>
    public int? LightingFps { get; set; }
    /// <summary>Beat detection for all effects; see BeatTuning. Null uses the defaults (0.5 and 0 ms).</summary>
    public float? BeatSensitivity { get; set; }
    public int? BeatOffsetMs { get; set; }
}

public sealed class Profile
{
    public string Name { get; set; } = "Default";
    public EffectSpec Effect { get; set; } = new();
    public List<FanCurveConfig> FanCurves { get; set; } = [];
    public LcdSettings Lcd { get; set; } = new();
    public KeyboardSettings Keyboard { get; set; } = new();
}

/// <summary>A built-in keyboard mode, or null Mode to follow the lighting effect.</summary>
public sealed class KeyboardSettings
{
    public byte? Mode { get; set; }
    public string Color { get; set; } = "#ffffff";
    /// <summary>0 fastest to 5 slowest.</summary>
    public byte Speed { get; set; } = 3;
    public bool Random { get; set; }
    /// <summary>Firmware direction: 0 left to right; 1 (also 2 and 3) right to left.</summary>
    public byte Direction { get; set; }
    public KeyboardSettings Clone() => (KeyboardSettings)MemberwiseClone();
}

public sealed class LcdSettings
{
    /// <summary>"dashboard" (built-in screen), "media" (uploaded file) or "device" (the AIO's own content; nothing is sent).</summary>
    public string Mode { get; set; } = "dashboard";
    /// <summary>Media file id when Mode is "media".</summary>
    public string? Media { get; set; }
    /// <summary>Built-in screen id when Mode is "dashboard".</summary>
    public string Screen { get; set; } = "status";
    /// <summary>Lighting screens: "case" shows the whole case map, "pump" the area around the pump zone.</summary>
    public string BackgroundArea { get; set; } = "case";
    /// <summary>Lighting behind the built-in screens: "off", "flow" or "bar".</summary>
    public string Background { get; set; } = "off";
    /// <summary>Bar: moves top to bottom instead of left to right.</summary>
    public bool BarVertical { get; set; }
    /// <summary>Bar: seconds per sweep.</summary>
    public float BarSeconds { get; set; } = 3;
    /// <summary>Bar: width as a share of the screen.</summary>
    public float BarWidth { get; set; } = 0.3f;
    /// <summary>Darkening of the background under dials and text, 0..0.9.</summary>
    public float BackgroundDim { get; set; } = 0.35f;
    /// <summary>
    /// Most frames per second sent to the LCD, 0.5..10. Tested on a TITAN 360: 10 fps with animated lighting made
    /// the pump head drop off USB within minutes; 2 fps after 12 minutes; 1 fps ran without drops.
    /// </summary>
    public float MaxFps { get; set; } = 1;

    /// <summary>Chosen metric ids for each screen's data slots, by screen id. Missing entries use the screen's defaults.</summary>
    public Dictionary<string, List<string>> Data { get; set; } = [];
    /// <summary>Media mode: draws the temperatures over the media.</summary>
    public bool MediaOverlay { get; set; }

    public LcdSettings Clone()
    {
        var c = (LcdSettings)MemberwiseClone();
        c.Data = Data.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        return c;
    }
    public byte Brightness { get; set; } = 100;
    public byte Rotation { get; set; }
}

/// <summary>Reads and writes settings.json in the data directory. Writes go to a temporary file first.</summary>
public sealed class SettingsStore(string dataDirectory)
{
    public string DataDirectory { get; } = dataDirectory;
    public string MediaDirectory => Path.Combine(DataDirectory, "media");
    private string FilePath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Loads settings. The first run seeds one profile from the curves and layout in rgbblaster.json.</summary>
    public RuntimeSettings Load(AppConfig seed)
    {
        Directory.CreateDirectory(MediaDirectory);
        RuntimeSettings? s = null;
        if (File.Exists(FilePath))
            s = JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(FilePath), AppConfig.JsonOptions);
        s ??= new RuntimeSettings { Layout = seed.Layout };
        if (s.Profiles.Count == 0)
            s.Profiles.Add(new Profile { Name = "Default", FanCurves = seed.FanCurves });
        if (!s.Profiles.Any(p => p.Name == s.ActiveProfile)) s.ActiveProfile = s.Profiles[0].Name;
        return s;
    }

    public void Save(RuntimeSettings s)
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, AppConfig.JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
