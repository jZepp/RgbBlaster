using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>Builds effects from <see cref="EffectSpec"/>. Unknown types fall back to the rainbow wave.</summary>
public static class EffectFactory
{
    public static readonly IReadOnlyList<string> Types = ["solid", "breathe", "rainbow", "wave", "radial", "cycle", "gradient", "matrix", "fire", "spectrum", "beat"];
    public const int MaxColors = 16;

    /// <param name="audio">Sound levels for the sound effects and the "follow sound" option. Null means silence.</param>
    public static IEffect Create(EffectSpec s, IAudioSource? audio = null)
    {
        audio ??= SilentAudio.Instance;
        string variation = (s.Variation ?? "off").ToLowerInvariant();
        float amount = Math.Clamp(s.VariationAmount, 0f, 180f);
        var palette = new Palette(Entries(s.PaletteOrDefault().Take(MaxColors)))
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
            "radial" => (s.Sound ?? "").Equals("rings", StringComparison.OrdinalIgnoreCase)
                ? new RadialEffect(palette, speed, scale, s.CenterX, s.CenterY, s.Spacing, audio, s.SoundAmount)
                : new RadialEffect(palette, speed, scale, s.CenterX, s.CenterY, s.Spacing),
            // Settings saved before palettes existed cycle the full hue wheel.
            "cycle" => s.Colors.Count > 0 ? new ColorCycleEffect(palette, speed) : new ColorCycleEffect(speed),
            "gradient" => new GradientEffect(palette, s.Angle),
            "matrix" => new MatrixEffect(palette, speed, s.Density),
            "fire" => new FireEffect(palette, speed, s.Height),
            "spectrum" => new SpectrumEffect(palette, audio),
            "beat" => new BeatPulseEffect(palette, audio, speed, s.CenterX, s.CenterY, s.RingWidth),
            _ => new RainbowWaveEffect(speed, scale),
        };
        if (variation == "drift" && amount > 0 && e is not RainbowWaveEffect)
            e = new ColorDriftEffect(e, palette, amount, Math.Clamp(s.VariationSeconds, 1f, 600f));
        string sound = (s.Sound ?? "off").ToLowerInvariant();
        if (sound is "brightness" or "speed" or "beat" && s.Type is not ("spectrum" or "beat"))
            e = new SoundFollowEffect(e, audio, sound, s.SoundAmount);
        float b = Math.Clamp(s.Brightness, 0f, 1f);
        return b >= 0.999f ? e : new BrightnessEffect(e, b);
    }

    /// <summary>Palette entries: "random" becomes null (a random color); entries that do not parse are dropped.</summary>
    private static IEnumerable<Rgb?> Entries(IEnumerable<string> colors)
    {
        foreach (var c in colors)
        {
            if (string.Equals(c, RandomColor, StringComparison.OrdinalIgnoreCase)) { yield return null; continue; }
            if (TryParse(c) is { } rgb) yield return rgb;
        }
    }

    /// <summary>Palette entry for a color that is random each time its turn comes round.</summary>
    public const string RandomColor = "random";

    private static Rgb? TryParse(string hex)
    {
        try { return Rgb.Parse(hex); } catch (FormatException) { return null; }
    }
}
