using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>2D frame that effects draw into. Devices sample it at their LED positions.</summary>
public sealed class Canvas(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public Rgb[] Pixels { get; } = new Rgb[width * height];

    public Span<Rgb> Row(int y) => Pixels.AsSpan(y * Width, Width);

    public void Fill(Rgb c) => Pixels.AsSpan().Fill(c);
}

public interface IEffect
{
    string Name { get; }
    /// <param name="t">Seconds since the effect started.</param>
    void Render(Canvas canvas, double t);
    /// <summary>The same effect as parameters, for devices that run it in firmware. None if no firmware equivalent.</summary>
    NativeEffect Native => NativeEffect.None;
}
