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
    public KeyboardSettings Clone() => (KeyboardSettings)MemberwiseClone();
}

public sealed class LcdSettings
{
    /// <summary>"dashboard" or "media".</summary>
    public string Mode { get; set; } = "dashboard";
    /// <summary>Media file id when Mode is "media".</summary>
    public string? Media { get; set; }
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
