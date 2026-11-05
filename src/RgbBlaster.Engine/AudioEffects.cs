using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>
/// Finds beats in the bass level: a beat is a bass level clearly above its average of the last second.
/// Feed it once per frame.
/// </summary>
public sealed class BeatDetector
{
    private float _avg;
    private double _lastBeat = double.NegativeInfinity;

    /// <summary>Shortest time between beats, seconds. 0.22 s allows up to about 270 beats per minute.</summary>
    public float MinInterval { get; init; } = 0.22f;

    public bool Update(float bass, float dt, double t)
    {
        bool beat = bass > 0.2f && bass > _avg * 1.3f + 0.05f && t - _lastBeat >= MinInterval;
        _avg += (bass - _avg) * (1 - MathF.Exp(-dt / 1f));
        if (beat) _lastBeat = t;
        return beat;
    }
}

/// <summary>
/// Spectrum analyzer across the case: bass at the front (x = 0), treble at the rear.
/// Bars rise from the bottom; colors run through the palette from the bottom of a bar to the top.
/// </summary>
public sealed class SpectrumEffect(Palette colors, IAudioSource audio) : IEffect
{
    private float[] _shown = new float[AudioFrame.BandCount];
    private double _last = double.NaN;

    public string Name => "spectrum";

    public void Render(Canvas canvas, double t)
    {
        float dt = double.IsNaN(_last) ? 0 : (float)Math.Clamp(t - _last, 0, 0.2);
        _last = t;
        var bands = audio.Current.Bands;
        if (_shown.Length != bands.Length) _shown = new float[bands.Length];
        // Bars jump up at once and fall at 1.5 screen heights per second.
        for (int i = 0; i < bands.Length; i++) _shown[i] = MathF.Max(bands[i], _shown[i] - 1.5f * dt);

        int w = canvas.Width, h = canvas.Height, n = _shown.Length;
        for (int y = 0; y < h; y++)
        {
            var row = canvas.Row(y);
            float up = 1 - (y + 0.5f) / h;          // 0 at the bottom, 1 at the top
            var color = colors.Span(up);
            for (int x = 0; x < w; x++)
            {
                // Smooth between neighboring bands, so the bars blend on lights between two bands.
                float p = (x + 0.5f) / w * n - 0.5f;
                int i = Math.Clamp((int)MathF.Floor(p), 0, n - 1), j = Math.Min(i + 1, n - 1);
                float f = Math.Clamp(p - i, 0, 1);
                float level = _shown[i] + (_shown[j] - _shown[i]) * f;
                // Soft top edge, a tenth of the height, ending at the bar level: a silent band stays dark.
                float k = Math.Clamp((level - up) * 10, 0, 1);
                row[x] = k <= 0 ? Rgb.Black : color.Scale(k);
            }
        }
    }
}

/// <summary>
/// A ring grows out of the center point on each beat and fades as it travels. Each ring takes the next palette color,
/// so random variation gives each beat its own hue.
/// </summary>
/// <param name="speedHz">Ring speed: the ring crosses about three quarters of the case in 1 / (4 × speedHz) seconds.</param>
public sealed class BeatPulseEffect(Palette colors, IAudioSource audio, float speedHz, float cx, float cy, float width) : IEffect
{
    private const int MaxRings = 8;
    private readonly BeatDetector _beats = new();
    private readonly List<(double Start, long Slot)> _rings = [];
    private long _count;
    private double _last = double.NaN;

    public string Name => "beat";

    public void Render(Canvas canvas, double t)
    {
        float dt = double.IsNaN(_last) ? 0 : (float)Math.Clamp(t - _last, 0, 0.2);
        _last = t;
        var frame = audio.Current;
        if (_beats.Update(frame.Bass, dt, t))
        {
            _rings.Add((t, _count++));
            if (_rings.Count > MaxRings) _rings.RemoveAt(0);
        }
        float speed = Math.Max(0.05f, speedHz * 4), maxR = 0.85f, ringW = Math.Clamp(width, 0.02f, 0.4f);
        _rings.RemoveAll(r => (t - r.Start) * speed > maxR + ringW);

        // Faint glow in the last ring's color, following the loudness, so the case is not dark between beats.
        var glow = _count > 0 ? colors[_count - 1].Scale(0.15f * frame.Level) : Rgb.Black;
        int w = canvas.Width, h = canvas.Height;
        Span<(float R, float Fade, Rgb C)> rings = stackalloc (float, float, Rgb)[_rings.Count];
        for (int i = 0; i < _rings.Count; i++)
        {
            float r = (float)(t - _rings[i].Start) * speed;
            rings[i] = (r, 1 - r / (maxR + ringW), colors[_rings[i].Slot]);
        }
        for (int y = 0; y < h; y++)
        {
            var row = canvas.Row(y);
            float dy = (y + 0.5f) / h - cy;
            for (int x = 0; x < w; x++)
            {
                float dx = (x + 0.5f) / w - cx, d = MathF.Sqrt(dx * dx + dy * dy);
                float best = 0;
                Rgb c = glow;
                foreach (var ring in rings)
                {
                    float k = (1 - MathF.Abs(d - ring.R) / ringW) * ring.Fade;
                    if (k > best) { best = k; c = ring.C.Scale(k); }
                }
                row[x] = best > 0.15f * frame.Level ? c : glow;
            }
        }
    }
}

/// <summary>Makes any effect follow the sound: its brightness, its speed, or a flash on each beat.</summary>
/// <param name="mode">"brightness", "speed" or "beat".</param>
/// <param name="amount">0..1: how much the sound changes the effect. 1 means dark (or stopped) in silence.</param>
public sealed class SoundFollowEffect(IEffect inner, IAudioSource audio, string mode, float amount) : IEffect
{
    private readonly BeatDetector _beats = new();
    private double _last = double.NaN, _warped;
    private float _level, _flash;

    public string Name => inner.Name;
    public NativeEffect Native => inner.Native;

    public void Render(Canvas canvas, double t)
    {
        float dt = double.IsNaN(_last) ? 0 : (float)Math.Clamp(t - _last, 0, 0.2);
        _last = t;
        var frame = audio.Current;
        // Fast rise, 0.2 s fall, so the lights do not flicker with every sample.
        _level = frame.Level > _level ? frame.Level : _level + (frame.Level - _level) * (1 - MathF.Exp(-dt / 0.2f));
        float a = Math.Clamp(amount, 0, 1);
        switch (mode)
        {
            case "speed":
                // From 1 - amount of normal speed in silence up to about twice normal speed at full level.
                _warped += dt * ((1 - a) + a * 2.2f * _level);
                inner.Render(canvas, _warped);
                return;
            case "beat":
                _flash = _beats.Update(frame.Bass, dt, t) ? 1 : _flash * MathF.Exp(-dt * 5);
                inner.Render(canvas, t);
                Dim(canvas, (1 - a) + a * _flash);
                return;
            default:
                inner.Render(canvas, t);
                Dim(canvas, (1 - a) + a * _level);
                return;
        }
    }

    private static void Dim(Canvas canvas, float k)
    {
        if (k >= 0.999f) return;
        var px = canvas.Pixels;
        for (int i = 0; i < px.Length; i++) px[i] = px[i].Scale(k);
    }
}
