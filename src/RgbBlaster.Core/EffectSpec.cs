namespace RgbBlaster.Core;

/// <summary>
/// An effect as data, so profiles can store it and the web GUI can edit it. Unused fields are ignored by each type.
/// Types: solid, breathe, rainbow, wave, radial, cycle, gradient.
/// </summary>
public sealed class EffectSpec
{
    public string Type { get; set; } = "rainbow";
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

    public EffectSpec Clone() => (EffectSpec)MemberwiseClone();
}
