namespace RgbBlaster.Core;

/// <summary>
/// An effect as data, so profiles can store it and the web GUI can edit it. Unused fields are ignored by each type.
/// Types: solid, breathe, rainbow, wave, radial, cycle, gradient, matrix, fire.
/// </summary>
public sealed class EffectSpec
{
    public string Type { get; set; } = "rainbow";
    /// <summary>Palette, in order. Empty means Color then Color2 (settings saved before palettes existed).</summary>
    public List<string> Colors { get; set; } = [];
    public string Color { get; set; } = "#00A0FF";
    public string Color2 { get; set; } = "#FF00A0";
    /// <summary>Cycles per second.</summary>
    public float Speed { get; set; } = 0.2f;
    /// <summary>Direction of travel in degrees: 0 = towards the rear (x+), 90 = downwards (y+).</summary>
    public float Angle { get; set; }
    /// <summary>Number of repeats across the canvas.</summary>
    public float Scale { get; set; } = 1f;
    /// <summary>0..1, applied to every effect.</summary>
    public float Brightness { get; set; } = 1f;
    /// <summary>Canvas point for radial effects, 0..1.</summary>
    public float CenterX { get; set; } = 0.5f;
    public float CenterY { get; set; } = 0.5f;
    /// <summary>Matrix: share of columns falling at one time, 0..1.</summary>
    public float Density { get; set; } = 0.4f;
    /// <summary>Wave and radial: dark share of each color band, 0..0.95.</summary>
    public float Spacing { get; set; }
    /// <summary>Fire: flame height as a share of the canvas, 0.05..1.</summary>
    public float Height { get; set; } = 0.6f;

    /// <summary>The palette, falling back to Color and Color2.</summary>
    public IReadOnlyList<string> PaletteOrDefault() => Colors.Count > 0 ? Colors : [Color, Color2];

    public EffectSpec Clone()
    {
        var c = (EffectSpec)MemberwiseClone();
        c.Colors = [.. Colors];
        return c;
    }
}
