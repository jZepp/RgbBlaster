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
    public NativeEffect Native => new(NativeEffectKind.Rainbow, PeriodSeconds: 1f / speedHz);

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
    public NativeEffect Native => new(NativeEffectKind.Breathe, color, periodSeconds);

    public void Render(Canvas canvas, double t)
    {
        float k = 0.5f - 0.5f * MathF.Cos((float)(t * 2 * Math.PI / periodSeconds));
        canvas.Fill(color.Scale(k));
    }
}

/// <summary>
/// Two-color band that travels across the canvas in any direction. Uses canvas geometry, so the layout decides
/// which lights see the band first.
/// </summary>
public sealed class WaveEffect(Rgb a, Rgb b, float speedHz, float angleDegrees, float wavelengths) : IEffect
{
    public string Name => "wave";

    public void Render(Canvas canvas, double t)
    {
        float rad = angleDegrees * MathF.PI / 180f;
        float dx = MathF.Cos(rad), dy = MathF.Sin(rad);
        float phase = (float)(t * speedHz);
        float w = canvas.Width, h = canvas.Height;
        for (int y = 0; y < canvas.Height; y++)
        {
            var row = canvas.Row(y);
            for (int x = 0; x < canvas.Width; x++)
            {
                float d = (x / w * dx + y / h * dy) * wavelengths - phase;
                float k = 0.5f - 0.5f * MathF.Cos(d * 2 * MathF.PI);
                row[x] = Lerp(a, b, k);
            }
        }
    }

    internal static Rgb Lerp(Rgb a, Rgb b, float k) =>
        new((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
}

/// <summary>Rings that grow out of a centre point.</summary>
public sealed class RadialEffect(Rgb a, Rgb b, float speedHz, float rings, float cx, float cy) : IEffect
{
    public string Name => "radial";

    public void Render(Canvas canvas, double t)
    {
        float phase = (float)(t * speedHz);
        float w = canvas.Width, h = canvas.Height;
        for (int y = 0; y < canvas.Height; y++)
        {
            var row = canvas.Row(y);
            for (int x = 0; x < canvas.Width; x++)
            {
                float ddx = x / w - cx, ddy = y / h - cy;
                float d = MathF.Sqrt(ddx * ddx + ddy * ddy) * rings - phase;
                float k = 0.5f - 0.5f * MathF.Cos(d * 2 * MathF.PI);
                row[x] = WaveEffect.Lerp(a, b, k);
            }
        }
    }
}

/// <summary>Every light shows the same hue, which turns through the color wheel.</summary>
public sealed class ColorCycleEffect(float speedHz) : IEffect
{
    public string Name => "cycle";
    public void Render(Canvas canvas, double t) => canvas.Fill(Rgb.FromHsv((float)(t * speedHz * 360.0), 1f, 1f));
}

/// <summary>Scales another effect's output.</summary>
public sealed class BrightnessEffect(IEffect inner, float brightness) : IEffect
{
    public string Name => inner.Name;
    public NativeEffect Native => inner.Native;

    public void Render(Canvas canvas, double t)
    {
        inner.Render(canvas, t);
        var p = canvas.Pixels;
        for (int i = 0; i < p.Length; i++) p[i] = p[i].Scale(brightness);
    }
}
