using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>
/// Beat settings shared by every effect: they depend on the music and the speakers, not on the effect.
/// The service sets <see cref="Shared"/> from the saved settings.
/// </summary>
public sealed class BeatTuning
{
    public static BeatTuning Shared { get; } = new();

    private volatile float _sensitivity = 0.5f;
    private volatile int _offsetMs;

    /// <summary>0..1. 0.5 is the default; higher finds softer beats, lower only the strongest kicks.</summary>
    public float Sensitivity { get => _sensitivity; set => _sensitivity = Math.Clamp(value, 0f, 1f); }

    /// <summary>
    /// Milliseconds, -250..250. Positive delays each beat. Negative shows each beat early: when the beats are steady,
    /// the next one is predicted from the tempo and shown that much before it is heard.
    /// </summary>
    public int OffsetMs { get => _offsetMs; set => _offsetMs = Math.Clamp(value, -250, 250); }
}

/// <summary>
/// Finds beats as sudden rises in the bass bands (onsets), not as a high bass level.
/// The tray's auto-gain keeps loud music near full scale, so a level test stops firing after the first second;
/// the rise from one sound frame to the next still shows each kick.
/// A beat is a rise well above the usual rise of the last second. Feed it once per rendered frame.
/// <see cref="BeatTuning.OffsetMs"/> moves the beats later (a delay) or earlier (predicted from the tempo).
/// </summary>
public sealed class BeatDetector(BeatTuning? tuning = null)
{
    private const int BassBands = 3, History = 8;
    private readonly BeatTuning _tuning = tuning ?? BeatTuning.Shared;
    private readonly float[] _prev = new float[BassBands];
    private AudioFrame? _frame;
    private float _mean, _square;
    private double _lastOnset = double.NegativeInfinity, _lastT = double.NaN, _lastShown = double.NegativeInfinity;
    private double _predictedFor = double.NegativeInfinity;
    private readonly List<double> _onsets = [];
    private readonly Queue<double> _due = new();

    /// <summary>Shortest time between beats, seconds. 0.22 s allows up to about 270 beats per minute.</summary>
    public float MinInterval { get; init; } = 0.22f;

    /// <summary>Beat period in seconds while the beats are steady, else null.</summary>
    public double? Period { get; private set; }

    /// <summary>True on the frame where a beat is to be shown.</summary>
    public bool Update(AudioFrame frame, double t)
    {
        bool onset = !ReferenceEquals(frame, _frame) && Onset(frame, t);
        if (onset) Remember(t);
        double offset = _tuning.OffsetMs / 1000.0;
        if (offset >= 0)
        {
            if (onset) _due.Enqueue(t + offset);
            if (_due.Count == 0 || t < _due.Peek()) return false;
            while (_due.Count > 0 && t >= _due.Peek()) _due.Dequeue();
            return Show(t);
        }

        double lead = -offset;
        _due.Clear();
        if (Period is not { } p) return onset && Show(t);
        if (onset)
        {
            // The heard beat that a prediction already showed: do not show it twice.
            if (t - _lastShown < p * 0.5) return false;
            return Show(t);
        }
        // Next beats predicted from the last heard one. Stop after four missing beats: the music has changed.
        int k = (int)Math.Floor((t + lead - _lastOnset) / p);
        if (k < 1 || k > 4) return false;
        double next = _lastOnset + k * p;
        if (next <= _predictedFor + p * 0.5 || t - (next - lead) > 0.1 || t - _lastShown < p * 0.5) return false;
        _predictedFor = next;
        return Show(t);
    }

    private bool Show(double t)
    {
        _lastShown = t;
        return true;
    }

    private bool Onset(AudioFrame frame, double t)
    {
        bool first = _frame is null;
        _frame = frame;
        float dt = double.IsNaN(_lastT) ? 0 : (float)Math.Clamp(t - _lastT, 0, 0.2);
        _lastT = t;

        var bands = frame.Bands;
        int n = Math.Min(BassBands, bands.Length);
        float rise = 0;
        for (int i = 0; i < n; i++)
        {
            if (!first) rise += MathF.Max(0, bands[i] - _prev[i]);
            _prev[i] = bands[i];
        }
        if (n > 0) rise /= n;

        // Sensitivity 0.5: a rise 2 standard deviations above the usual rise and at least 6% of full scale.
        float s = _tuning.Sensitivity, deviations = 3 - 2 * s, floor = 0.06f * (1.5f - s);
        float sd = MathF.Sqrt(MathF.Max(0, _square - _mean * _mean));
        bool beat = rise > _mean + deviations * sd && rise > floor && t - _lastOnset >= MinInterval;
        float k = 1 - MathF.Exp(-dt / 1f);
        _mean += (rise - _mean) * k;
        _square += (rise * rise - _square) * k;
        return beat;
    }

    /// <summary>
    /// Keeps the recent onsets and finds the tempo. Music has off-beats and fills between the kicks, so the gaps are not
    /// all equal. Instead, a beat grid that starts at the last onset is fitted: the period, 0.3..1.05 s, that puts the most
    /// of the last 8 onsets within 8% of a grid line. Steady when at least 6 of the 8 fit.
    /// </summary>
    private void Remember(double t)
    {
        _lastOnset = t;
        _onsets.Add(t);
        if (_onsets.Count > History) _onsets.RemoveAt(0);
        Period = null;
        if (_onsets.Count < History) return;
        int bestHits = 0;
        double best = 0;
        for (double p = 0.3; p <= 1.05; p += 0.0025)
        {
            int hits = Hits(p);
            if (hits > bestHits) { bestHits = hits; best = p; }
        }
        // A grid at half the beat also fits every beat; take the full beat when it fits nearly as well.
        if (best < 0.525 && Hits(best * 2) >= bestHits - 2) { best *= 2; bestHits = Hits(best); }
        if (bestHits >= History - 2) Period = best;
    }

    private int Hits(double period)
    {
        int hits = 0;
        foreach (var x in _onsets)
        {
            double phase = (_lastOnset - x) / period % 1;
            if (Math.Min(phase, 1 - phase) <= 0.08) hits++;
        }
        return hits;
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
        if (_beats.Update(frame, t))
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

/// <summary>Makes any effect follow the sound: its brightness, its speed, a flash on each beat, or a kick back on each beat.</summary>
/// <param name="mode">"brightness", "speed", "beat" or "kick".</param>
/// <param name="amount">0..1: how much the sound changes the effect. 1 means dark (or stopped) in silence.</param>
public sealed class SoundFollowEffect(IEffect inner, IAudioSource audio, string mode, float amount) : IEffect
{
    private readonly BeatDetector _beats = new();
    private double _last = double.NaN, _warped;
    private float _level, _flash, _push;

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
            case "kick":
                // Each beat pushes the effect backward: at full strength it runs back at twice normal speed,
                // then the push fades over about a quarter second and the effect goes forward again.
                // Net effect of one hit at full strength: about 0.75 s of motion undone.
                _push = _beats.Update(frame, t) ? 1 : _push * MathF.Exp(-dt / 0.25f);
                _warped += dt * (1 - a * 3 * _push);
                inner.Render(canvas, _warped);
                return;
            case "beat":
                _flash = _beats.Update(frame, t) ? 1 : _flash * MathF.Exp(-dt * 5);
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
