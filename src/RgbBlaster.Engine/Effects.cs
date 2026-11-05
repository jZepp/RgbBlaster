using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>One color everywhere. Follows the palette's drift.</summary>
public sealed class SolidEffect(Palette colors) : IEffect
{
    public SolidEffect(Rgb color) : this(Palette.Of(color)) { }

    public string Name => "solid";
    public void Render(Canvas canvas, double t) => canvas.Fill(colors.Span(0));
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

/// <summary>Fades in and out. With several colors, each breath uses the next color.</summary>
public sealed class BreathingEffect(Palette colors, float periodSeconds = 4f) : IEffect
{
    public BreathingEffect(Rgb color, float periodSeconds = 4f) : this(Palette.Of(color), periodSeconds) { }

    public string Name => "breathe";
    public NativeEffect Native => new(NativeEffectKind.Breathe, colors[0], periodSeconds, Random: colors.Count > 1);

    public void Render(Canvas canvas, double t)
    {
        double cycles = t / periodSeconds;
        float k = 0.5f - 0.5f * MathF.Cos((float)(cycles * 2 * Math.PI));
        canvas.Fill(colors[(int)Math.Floor(cycles)].Scale(k));
    }
}

/// <summary>
/// Bands of the palette colors that travel across the canvas in any direction. Uses canvas geometry, so the layout decides
/// which lights see a band first.
/// </summary>
/// <param name="spacing">Dark share of each color band, 0..0.95. See <see cref="Palette.Bands"/>.</param>
public sealed class WaveEffect(Palette colors, float speedHz, float angleDegrees, float wavelengths, float spacing = 0) : IEffect
{
    public WaveEffect(Rgb a, Rgb b, float speedHz, float angleDegrees, float wavelengths) : this(Palette.Of(a, b), speedHz, angleDegrees, wavelengths) { }

    public string Name => "wave";
    public NativeEffect Native => new(NativeEffectKind.Wave, colors[0], speedHz > 0 ? 1f / speedHz : 0, Random: colors.Count > 2, AngleDegrees: angleDegrees);

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
                row[x] = colors.Bands((x / w * dx + y / h * dy) * wavelengths - phase, spacing);
        }
    }
}

/// <summary>Still blend through the palette from one side of the canvas to the other.</summary>
public sealed class GradientEffect(Palette colors, float angleDegrees) : IEffect
{
    public string Name => "gradient";

    public void Render(Canvas canvas, double t)
    {
        float rad = angleDegrees * MathF.PI / 180f;
        float dx = MathF.Cos(rad), dy = MathF.Sin(rad);
        // Project the canvas corners so the first and last colors land on the far edges.
        float lo = MathF.Min(0, dx) + MathF.Min(0, dy), hi = MathF.Max(0, dx) + MathF.Max(0, dy);
        float w = canvas.Width, h = canvas.Height;
        for (int y = 0; y < canvas.Height; y++)
        {
            var row = canvas.Row(y);
            for (int x = 0; x < canvas.Width; x++)
                row[x] = colors.Span(((x + 0.5f) / w * dx + (y + 0.5f) / h * dy - lo) / (hi - lo));
        }
    }
}

/// <summary>Rings of the palette colors that grow out of a center point.</summary>
public sealed class RadialEffect(Palette colors, float speedHz, float rings, float cx, float cy, float spacing = 0) : IEffect
{
    public string Name => "radial";
    public NativeEffect Native => new(NativeEffectKind.Wave, colors[0], speedHz > 0 ? 1f / speedHz : 0, Random: colors.Count > 2);

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
                row[x] = colors.Bands(MathF.Sqrt(ddx * ddx + ddy * ddy) * rings - phase, spacing);
            }
        }
    }
}

/// <summary>Every light shows the same color, which blends through the palette in turn.</summary>
public sealed class ColorCycleEffect(Palette colors, float speedHz) : IEffect
{
    /// <summary>Full hue wheel.</summary>
    public ColorCycleEffect(float speedHz) : this(new Palette(Enumerable.Range(0, 6).Select(i => Rgb.FromHsv(i * 60, 1, 1))), speedHz) { }

    public string Name => "cycle";
    public NativeEffect Native => new(NativeEffectKind.Cycle, PeriodSeconds: speedHz > 0 ? 1f / speedHz : 0);
    public void Render(Canvas canvas, double t) => canvas.Fill(colors.Loop((float)(t * speedHz)));
}

/// <summary>
/// Falling streaks, like code rain. Each column of cells has its own drop with a bright head and a fading tail.
/// Drops take palette colors at random. Density is roughly the share of columns falling at one time.
/// </summary>
public sealed class MatrixEffect : IEffect
{
    private const int CellPx = 8;
    private readonly Palette _colors;
    private readonly float _speed, _density;
    private readonly Random _rng = new(1234);
    private Drop[] _drops = [];
    private double _last;

    private struct Drop { public float Head, Speed, Tail; public int Color; public bool Active; }

    /// <param name="speedHz">About how many canvas heights a drop falls per second.</param>
    /// <param name="density">0..1.</param>
    public MatrixEffect(Palette colors, float speedHz, float density)
    {
        _colors = colors;
        _speed = speedHz;
        _density = Math.Clamp(density, 0.05f, 1f);
    }

    private Drop NewDrop(int rows, float head) => new()
    {
        Active = true,
        Head = head,
        Speed = _speed * (0.6f + 0.8f * _rng.NextSingle()),
        Tail = rows * (0.3f + 0.5f * _rng.NextSingle()),
        Color = _rng.Next(), // band slot: palette color slot mod Count, its own hue with random variation
    };

    public string Name => "matrix";
    public NativeEffect Native => new(NativeEffectKind.Sparkle, _colors[0], Random: _colors.Count > 1);

    public void Render(Canvas canvas, double t)
    {
        int cols = Math.Max(1, canvas.Width / CellPx), rows = Math.Max(1, canvas.Height / CellPx);
        if (_drops.Length != cols)
        {
            // Start part-way through, so the first frame already looks like rain.
            _drops = new Drop[cols];
            _last = t;
            for (int c = 0; c < cols; c++)
                if (_rng.NextSingle() < _density) _drops[c] = NewDrop(rows, _rng.NextSingle() * rows * 1.5f);
        }
        float dt = (float)Math.Clamp(t - _last, 0, 0.25);
        _last = t;

        for (int c = 0; c < cols; c++)
        {
            ref var d = ref _drops[c];
            if (d.Active)
            {
                d.Head += d.Speed * dt * rows;
                if (d.Head - d.Tail > rows) d.Active = false;
            }
            // Idle columns start a drop at a rate that keeps about `density` of them busy.
            else if (_rng.NextSingle() < _density * dt * _speed * 2)
                d = NewDrop(rows, 0);
        }

        canvas.Fill(Rgb.Black);
        for (int c = 0; c < cols; c++)
        {
            var d = _drops[c];
            if (!d.Active) continue;
            var color = _colors[d.Color];
            var head = Palette.Blend(color, Rgb.White, 0.6f);
            int x0 = c * CellPx, cw = Math.Min(CellPx, canvas.Width - x0);
            for (int r = 0; r < rows; r++)
            {
                float behind = d.Head - r;
                if (behind < 0 || behind > d.Tail) continue;
                var px = behind < 1 ? head : color.Scale(1 - behind / d.Tail);
                for (int y = r * CellPx; y < (r + 1) * CellPx && y < canvas.Height; y++)
                    canvas.Row(y).Slice(x0, cw).Fill(px);
            }
        }
    }
}

/// <summary>
/// Flames that rise from the bottom of the canvas. A coarse heat grid: the bottom row is fed random heat, and each
/// step every cell takes the heat of a cell below it, shifted sideways at random, minus some cooling. Heat maps to the
/// palette from coolest to hottest color; low heat fades to black.
/// </summary>
public sealed class FireEffect : IEffect
{
    private const int CellPx = 4;
    private const float StepsPerSecondAtDefault = 30f;
    private readonly Palette _colors;
    private readonly float _stepsPerSecond, _height;
    private readonly Random _rng = new(4321);
    private float[] _heat = [];
    private int _gw, _gh;
    private double _pending, _last = double.NaN;

    /// <param name="speedHz">Flicker rate; the default 0.2 gives 30 simulation steps per second.</param>
    /// <param name="height">Flame height as a share of the canvas, 0.05..1.</param>
    public FireEffect(Palette colors, float speedHz, float height)
    {
        _colors = colors;
        _stepsPerSecond = StepsPerSecondAtDefault * Math.Clamp(speedHz / 0.2f, 0.3f, 3f);
        _height = Math.Clamp(height, 0.05f, 1f);
    }

    public string Name => "fire";
    public NativeEffect Native => new(NativeEffectKind.Sparkle, _colors[Math.Max(0, _colors.Count / 2)]);

    public void Render(Canvas canvas, double t)
    {
        int gw = Math.Max(2, canvas.Width / CellPx), gh = Math.Max(2, canvas.Height / CellPx);
        if (gw != _gw || gh != _gh) { _gw = gw; _gh = gh; _heat = new float[gw * gh]; _last = double.NaN; }
        if (double.IsNaN(_last)) { _last = t; for (int i = 0; i < 60; i++) Step(); }
        _pending += Math.Clamp(t - _last, 0, 0.5) * _stepsPerSecond;
        _last = t;
        for (; _pending >= 1; _pending--) Step();

        for (int y = 0; y < canvas.Height; y++)
        {
            var row = canvas.Row(y);
            int gy = Math.Min(gh - 1, y / CellPx);
            for (int x = 0; x < canvas.Width; x++)
            {
                float h = _heat[gy * gw + Math.Min(gw - 1, x / CellPx)];
                row[x] = h <= 0.02f ? Rgb.Black : _colors.Span(h).Scale(Math.Min(1f, h * 2.5f));
            }
        }
    }

    private void Step()
    {
        int gw = _gw, gh = _gh;
        // Fuel: the bottom row flickers between strong and full heat, with occasional gaps that split the flames.
        int bottom = (gh - 1) * gw;
        for (int x = 0; x < gw; x++)
            _heat[bottom + x] = _rng.NextSingle() < 0.25f ? 0.15f : 0.8f + 0.2f * _rng.NextSingle();
        // Cooling per row so that the tallest flames reach about `height` of the canvas.
        float cool = 0.85f / (gh * _height);
        for (int y = 0; y < gh - 1; y++)
            for (int x = 0; x < gw; x++)
            {
                int sx = Math.Clamp(x + _rng.Next(-1, 2), 0, gw - 1);
                float h = _heat[(y + 1) * gw + sx] - cool * _rng.NextSingle() * 2;
                _heat[y * gw + x] = h < 0 ? 0 : h;
            }
    }
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
