using RgbBlaster.Core;

namespace RgbBlaster.Engine;

public sealed class SolidEffect(Rgb color) : IEffect
{
    public string Name => "solid";
    public void Render(Canvas canvas, double t) => canvas.Fill(color);
}

/// <summary>Horizontal hue wave. Computes one hue per column and copies it down each row.</summary>
public sealed class RainbowWaveEffect(float speedHz = 0.2f, float wavelengths = 1f) : IEffect
{
    private Rgb[] _column = [];

    public string Name => "rainbow";

    public void Render(Canvas canvas, double t)
    {
        if (_column.Length != canvas.Width) _column = new Rgb[canvas.Width];
        float phase = (float)(t * speedHz * 360.0);
        float step = 360f * wavelengths / canvas.Width;
        for (int x = 0; x < canvas.Width; x++)
            _column[x] = Rgb.FromHsv(phase + x * step, 1f, 1f);
        for (int y = 0; y < canvas.Height; y++)
            _column.CopyTo(canvas.Row(y));
    }
}

public sealed class BreathingEffect(Rgb color, float periodSeconds = 4f) : IEffect
{
    public string Name => "breathe";

    public void Render(Canvas canvas, double t)
    {
        float k = 0.5f - 0.5f * MathF.Cos((float)(t * 2 * Math.PI / periodSeconds));
        canvas.Fill(color.Scale(k));
    }
}
