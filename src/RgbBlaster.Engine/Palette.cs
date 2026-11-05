using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>
/// An ordered list of colors that effects blend through. Never empty.
/// Optional variation moves each color's hue: per band at random (<see cref="RandomBands"/>), or for all colors over time (<see cref="SetDrift"/>).
/// </summary>
public sealed class Palette
{
    private readonly Rgb[] _base;
    private Rgb[] _colors;
    private float _drift;

    // Colors of recent band slots; a frame touches only a few slots, so a small cache avoids an HSV round trip per pixel.
    private const int CacheSize = 64;
    private readonly long[] _cacheSlot = new long[CacheSize];
    private readonly Rgb[] _cacheColor = new Rgb[CacheSize];

    public Palette(IEnumerable<Rgb> colors)
    {
        _base = colors.ToArray();
        if (_base.Length == 0) _base = [Rgb.White];
        _colors = _base;
        ClearCache();
    }

    public static Palette Of(params Rgb[] colors) => new(colors);

    /// <summary>
    /// Each band, breath or drop gets its own hue, up to <see cref="Variation"/> degrees from its palette color.
    /// The same band keeps its color while it moves.
    /// </summary>
    public bool RandomBands { get; init; }

    /// <summary>Largest hue change in degrees, 0..180. 180 means any hue, at full saturation, so white and gray vary too.</summary>
    public float Variation { get; init; }

    /// <summary>Random seed for <see cref="RandomBands"/>.</summary>
    public int Seed { get; init; } = 1;

    public int Count => _base.Length;

    /// <summary>Color of band slot <paramref name="slot"/>: palette color slot mod Count, varied when <see cref="RandomBands"/> is on.</summary>
    public Rgb this[long slot] => At(slot);
    public Rgb this[int i] => At(i);

    /// <summary>Turns every color's hue by <paramref name="degrees"/>. Effects call this once per frame through <see cref="ColorDriftEffect"/>.</summary>
    public void SetDrift(float degrees)
    {
        if (MathF.Abs(degrees - _drift) < 0.25f) return;
        _drift = degrees;
        _colors = degrees == 0 ? _base : _base.Select(c => Turn(c, degrees)).ToArray();
        ClearCache();
    }

    private Rgb At(long slot)
    {
        int n = _colors.Length;
        var c = _colors[(int)(((slot % n) + n) % n)];
        if (!RandomBands || Variation <= 0) return c;
        int k = (int)(slot & (CacheSize - 1));
        if (_cacheSlot[k] == slot) return _cacheColor[k];
        float r = Hash(slot);
        var v = Variation >= 180 ? Rgb.FromHsv(r * 360, 1, Value(c)) : Turn(c, (r * 2 - 1) * Variation);
        _cacheSlot[k] = slot;
        _cacheColor[k] = v;
        return v;
    }

    /// <summary>
    /// Position u in any range; one unit passes every color once and returns to the first, so the result loops without a seam.
    /// Neighboring colors blend with an S-curve, so each color holds briefly at its own position.
    /// </summary>
    public Rgb Loop(float u)
    {
        if (_colors.Length == 1 && !RandomBands) return _colors[0];
        float p = u * _colors.Length;
        long slot = (long)MathF.Floor(p);
        return Blend(At(slot), At(slot + 1), Smooth(p - slot));
    }

    /// <summary>
    /// Like <see cref="Loop"/>, but each color is a band that fades in and out with darkness between bands.
    /// <paramref name="spacing"/> is the dark share of each band's slot, 0..0.95. Zero gives <see cref="Loop"/>.
    /// </summary>
    public Rgb Bands(float u, float spacing)
    {
        if (spacing <= 0) return Loop(u);
        float p = u * _colors.Length;
        long slot = (long)MathF.Floor(p);
        float lit = 1 - Math.Clamp(spacing, 0f, 0.95f);
        float f = p - slot;
        if (f >= lit) return Rgb.Black;
        return At(slot).Scale(MathF.Sin(MathF.PI * f / lit));
    }

    /// <summary>Position u from 0 (first color) to 1 (last color), clamped. For gradients that do not loop. Follows drift only.</summary>
    public Rgb Span(float u)
    {
        int n = _colors.Length;
        if (n == 1) return _colors[0];
        float p = Math.Clamp(u, 0f, 1f) * (n - 1);
        int i = Math.Min((int)p, n - 2);
        return Blend(_colors[i], _colors[i + 1], p - i);
    }

    private void ClearCache() => Array.Fill(_cacheSlot, long.MinValue);

    /// <summary>0..1, the same for the same slot and seed.</summary>
    private float Hash(long slot)
    {
        ulong x = (ulong)slot * 0x9E3779B97F4A7C15UL ^ (ulong)Seed * 0xC2B2AE3D27D4EB4FUL;
        x ^= x >> 31; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 29;
        return (x >> 40) / (float)(1UL << 24);
    }

    private static float Value(Rgb c) => Math.Max(c.R, Math.Max(c.G, c.B)) / 255f;

    /// <summary>Turns the hue; saturation and value stay.</summary>
    internal static Rgb Turn(Rgb c, float degrees)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b)), d = max - min;
        if (d <= 0.0001f) return c; // gray has no hue
        float h = max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return Rgb.FromHsv(h + degrees, d / max, max);
    }

    private static float Smooth(float k) => k * k * (3 - 2 * k);

    internal static Rgb Blend(Rgb a, Rgb b, float k) =>
        new((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
}

/// <summary>Swings the palette's hue up to ±<paramref name="degrees"/> and back once per <paramref name="periodSeconds"/>.</summary>
public sealed class ColorDriftEffect(IEffect inner, Palette palette, float degrees, float periodSeconds) : IEffect
{
    public string Name => inner.Name;
    public NativeEffect Native => inner.Native;

    public void Render(Canvas canvas, double t)
    {
        // At 180 degrees the swing becomes a full turn round the hue wheel, so every hue appears.
        float k = (float)(t / Math.Max(1, periodSeconds));
        palette.SetDrift(degrees >= 180 ? k * 360 % 360 : degrees * MathF.Sin(k * 2 * MathF.PI));
        inner.Render(canvas, t);
    }
}
