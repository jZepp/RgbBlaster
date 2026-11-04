using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>Builds effects from <see cref="EffectSpec"/>. Unknown types fall back to the rainbow wave.</summary>
public static class EffectFactory
{
    public static readonly IReadOnlyList<string> Types = ["solid", "breathe", "rainbow", "wave", "radial", "cycle", "gradient"];

    public static IEffect Create(EffectSpec s)
    {
        var c1 = ParseOr(s.Color, new Rgb(0, 160, 255));
        var c2 = ParseOr(s.Color2, new Rgb(255, 0, 160));
        float speed = Math.Clamp(s.Speed, 0.01f, 10f);
        float scale = Math.Clamp(s.Scale, 0.1f, 20f);
        IEffect e = s.Type.ToLowerInvariant() switch
        {
            "solid" => new SolidEffect(c1),
            "breathe" => new BreathingEffect(c1, 1f / speed),
            "wave" => new WaveEffect(c1, c2, speed, s.Angle, scale),
            "radial" => new RadialEffect(c1, c2, speed, scale, s.CenterX, s.CenterY),
            "cycle" => new ColorCycleEffect(speed),
            "gradient" => new WaveEffect(c1, c2, 0, s.Angle, 0.5f),
            _ => new RainbowWaveEffect(speed, scale),
        };
        float b = Math.Clamp(s.Brightness, 0f, 1f);
        return b >= 0.999f ? e : new BrightnessEffect(e, b);
    }

    private static Rgb ParseOr(string hex, Rgb fallback)
    {
        try { return Rgb.Parse(hex); } catch (FormatException) { return fallback; }
    }
}
