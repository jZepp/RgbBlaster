using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>An ordered list of colors that effects blend through. Never empty.</summary>
public sealed class Palette
{
    private readonly Rgb[] _colors;

    public Palette(IEnumerable<Rgb> colors)
    {
        _colors = colors.ToArray();
        if (_colors.Length == 0) _colors = [Rgb.White];
    }

    public static Palette Of(params Rgb[] colors) => new(colors);

    public int Count => _colors.Length;
    public Rgb this[int i] => _colors[((i % _colors.Length) + _colors.Length) % _colors.Length];

    /// <summary>
    /// Position u in any range; one unit passes every color once and returns to the first, so the result loops without a seam.
    /// Neighbouring colors blend with an S-curve, so each color holds briefly at its own position.
    /// </summary>
    public Rgb Loop(float u)
    {
        int n = _colors.Length;
        if (n == 1) return _colors[0];
        float p = (u - MathF.Floor(u)) * n;
        int i = (int)p;
        if (i >= n) i = n - 1;
        return Blend(_colors[i], _colors[(i + 1) % n], Smooth(p - i));
    }

    /// <summary>Position u from 0 (first color) to 1 (last color), clamped. For gradients that do not loop.</summary>
    public Rgb Span(float u)
    {
        int n = _colors.Length;
        if (n == 1) return _colors[0];
        float p = Math.Clamp(u, 0f, 1f) * (n - 1);
        int i = Math.Min((int)p, n - 2);
        return Blend(_colors[i], _colors[i + 1], p - i);
    }

    private static float Smooth(float k) => k * k * (3 - 2 * k);

    internal static Rgb Blend(Rgb a, Rgb b, float k) =>
        new((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
}
