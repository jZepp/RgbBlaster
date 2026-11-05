using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>Builds effects from <see cref="EffectSpec"/>. Unknown types fall back to the rainbow wave.</summary>
public static class EffectFactory
{
    public static readonly IReadOnlyList<string> Types = ["solid", "breathe", "rainbow", "wave", "radial", "cycle", "gradient", "matrix", "fire"];
    public const int MaxColors = 16;

    public static IEffect Create(EffectSpec s)
    {
        string variation = (s.Variation ?? "off").ToLowerInvariant();
        float amount = Math.Clamp(s.VariationAmount, 0f, 180f);
        var palette = new Palette(s.PaletteOrDefault().Take(MaxColors).Select(TryParse).OfType<Rgb>())
        {
            RandomBands = variation == "random" && amount > 0,
            Variation = amount,
            Seed = Environment.TickCount,
        };
        float speed = Math.Clamp(s.Speed, 0.01f, 10f);
        float scale = Math.Clamp(s.Scale, 0.1f, 20f);
        IEffect e = s.Type.ToLowerInvariant() switch
        {
            "solid" => new SolidEffect(palette),
            "breathe" => new BreathingEffect(palette, 1f / speed),
            "wave" => new WaveEffect(palette, speed, s.Angle, scale, s.Spacing),
            "radial" => new RadialEffect(palette, speed, scale, s.CenterX, s.CenterY, s.Spacing),
            // Settings saved before palettes existed cycle the full hue wheel.
            "cycle" => s.Colors.Count > 0 ? new ColorCycleEffect(palette, speed) : new ColorCycleEffect(speed),
            "gradient" => new GradientEffect(palette, s.Angle),
            "matrix" => new MatrixEffect(palette, speed, s.Density),
            "fire" => new FireEffect(palette, speed, s.Height),
            _ => new RainbowWaveEffect(speed, scale),
        };
        if (variation == "drift" && amount > 0 && e is not RainbowWaveEffect)
            e = new ColorDriftEffect(e, palette, amount, Math.Clamp(s.VariationSeconds, 1f, 600f));
        float b = Math.Clamp(s.Brightness, 0f, 1f);
        return b >= 0.999f ? e : new BrightnessEffect(e, b);
    }

    private static Rgb? TryParse(string hex)
    {
        try { return Rgb.Parse(hex); } catch (FormatException) { return null; }
    }
}
