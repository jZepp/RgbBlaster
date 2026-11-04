using System.Runtime.InteropServices;

namespace RgbBlaster.Core;

/// <summary>24-bit color. Layout is R, G, B so a span of these can be copied to RGB-ordered wire buffers.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    /// <param name="h">Hue in degrees, any range.</param>
    /// <param name="s">Saturation 0..1.</param>
    /// <param name="v">Value 0..1.</param>
    public static Rgb FromHsv(float h, float s, float v)
    {
        h %= 360f;
        if (h < 0) h += 360f;
        float c = v * s;
        float x = c * (1 - MathF.Abs(h / 60f % 2 - 1));
        float m = v - c;
        (float r, float g, float b) = (int)(h / 60f) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new((byte)((r + m) * 255f + 0.5f), (byte)((g + m) * 255f + 0.5f), (byte)((b + m) * 255f + 0.5f));
    }

    public Rgb Scale(float f) =>
        new((byte)Math.Clamp(R * f, 0, 255), (byte)Math.Clamp(G * f, 0, 255), (byte)Math.Clamp(B * f, 0, 255));

    public static Rgb Parse(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) throw new FormatException($"Expected RRGGBB, got '{hex}'.");
        var v = Convert.ToUInt32(s, 16);
        return new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
