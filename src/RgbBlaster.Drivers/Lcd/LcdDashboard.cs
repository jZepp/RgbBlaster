using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using P = RgbBlaster.Drivers.Lcd.LinkLcdProtocol;

namespace RgbBlaster.Drivers.Lcd;

/// <summary>Live values for the built-in screens. Null means not available.</summary>
/// <param name="History">CPU and GPU temperatures, oldest first, one per second; seeds the history screen.</param>
public readonly record struct LcdValues(float? CpuC, float? GpuC, int? PumpRpm, float? LiquidC = null, float? CpuLoad = null, float? GpuLoad = null,
    IReadOnlyList<(float? Cpu, float? Gpu)>? History = null);

/// <summary>Packed RGB pixels, row by row, used as a screen background.</summary>
public sealed record LcdBackground(byte[] Rgb, int Width, int Height);

/// <summary>How the lighting background is drawn behind a built-in screen.</summary>
/// <param name="Mode">"off", "flow" (the whole background) or "bar" (a band of it that sweeps across).</param>
/// <param name="Vertical">Bar only: the band moves top to bottom instead of left to right.</param>
/// <param name="PeriodSeconds">Bar only: time for one sweep.</param>
/// <param name="BarWidth">Bar only: band width as a share of the screen, 0.05..1.</param>
/// <param name="Dim">Darkening under a screen's dials and text, 0..0.9. Not applied to the plain lighting screen.</param>
public sealed record LcdBackgroundStyle(string Mode = "off", bool Vertical = false, float PeriodSeconds = 3, float BarWidth = 0.3f, float Dim = 0.35f)
{
    public static readonly LcdBackgroundStyle Off = new();
    public bool On => Mode is "flow" or "bar";
}

/// <summary>
/// Renders the built-in LCD screens as 480x480 JPEG frames. Not thread-safe: use one instance per thread.
/// Animated screens ease their needles toward new values, so they are rendered several times per second.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class LcdDashboard : IDisposable
{
    /// <summary>Built-in screens and the interval between their frames. Lighting screens need a background.</summary>
    public static readonly IReadOnlyList<(string Id, string Name, int FrameMs, bool UsesLighting)> Screens =
    [
        ("status", "Status", 1000, false),
        ("speedometer", "Speedometer", 100, false),
        ("triple", "Triple dial", 100, false),
        ("rings", "Rings", 100, false),
        ("clock", "Clock", 1000, false),
        ("chrono", "Chronograph", 1000, false),
        ("coolant", "Coolant gauge", 100, false),
        ("twin", "Twin gauges", 100, false),
        ("history", "History", 1000, false),
        ("segments", "Segments", 1000, false),
        ("load", "Load", 100, false),
        ("lighting", "Lighting", 66, true),
        ("lighting-status", "Lighting + status", 66, true),
    ];

    /// <summary>Background size requested for lighting screens; the screen scales it up smoothly.</summary>
    public const int BackgroundSize = 64;

    private const float Cx = P.Width / 2f, Cy = P.Height / 2f;
    private const string Face = "Bahnschrift";

    private readonly Bitmap _bmp = new(P.Width, P.Height, PixelFormat.Format24bppRgb);
    private readonly ImageCodecInfo _jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
    private readonly EncoderParameters _quality = new(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 75L) } };
    private readonly Font _big = new("Segoe UI", 110, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _label = new("Segoe UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _small = new("Segoe UI", 38, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _dialNum = new(Face, 32, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _stat = new(Face, 42, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _tag = new(Face, 18, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _readout = new(Face, 64, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _unit = new(Face, 22, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _clock = new(Face, 120, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _clockSmall = new(Face, 30, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _small16 = new(Face, 16, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _tagSmall = new(Face, 15, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly StringFormat _center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private readonly StringFormat _centerOneLine = new(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    // Needle positions shown on animated screens; they ease toward the live values.
    private float _cpuShown = float.NaN, _gpuShown = float.NaN, _pumpShown = float.NaN;
    private readonly System.Diagnostics.Stopwatch _clockSw = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrame;

    /// <summary>Temperature range for gauges, °C.</summary>
    public float GaugeMin { get; set; } = 20;
    public float GaugeMax { get; set; } = 100;
    /// <summary>Start of the red zone on the speedometer, °C.</summary>
    public float RedLine { get; set; } = 85;
    public int PumpMaxRpm { get; set; } = 3200;

    public static int FrameMs(string screen) => Screens.FirstOrDefault(s => s.Id == screen).FrameMs is > 0 and var ms ? ms : 1000;
    public static bool UsesLighting(string screen) => Screens.Any(s => s.Id == screen && s.UsesLighting);

    /// <summary>Frame interval for a screen with the given background: a moving background needs the lighting frame rate.</summary>
    public static int FrameMs(string screen, LcdBackgroundStyle style) =>
        style.On ? Math.Min(FrameMs(screen), LightingFrameMs) : FrameMs(screen);

    /// <summary>True when the screen draws the lighting background, so the caller must supply it.</summary>
    public static bool NeedsBackground(string screen, LcdBackgroundStyle style) => style.On || UsesLighting(screen);

    public const int LightingFrameMs = 66;

    /// <summary>The original status screen.</summary>
    public byte[] Render(float? cpuC, float? gpuC, int? pumpRpm) => Render("status", new LcdValues(cpuC, gpuC, pumpRpm));

    /// <summary>Unknown screen ids show the status screen. <paramref name="background"/> is used by lighting screens.</summary>
    public byte[] Render(string screen, LcdValues v, LcdBackground? background = null) =>
        Render(screen, v, background, LcdBackgroundStyle.Off);

    /// <summary>
    /// Draws <paramref name="screen"/> over the lighting background as <paramref name="style"/> sets.
    /// The lighting screens always show the background; with style off they show all of it.
    /// </summary>
    /// <param name="slots">Metric ids for the screen's data slots; see <see cref="DefaultSlots"/>. Null uses the defaults.</param>
    public byte[] Render(string screen, LcdValues v, LcdBackground? background, LcdBackgroundStyle style, IReadOnlyList<string>? slots = null)
    {
        double now = _clockSw.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastFrame, 0, 1);
        _lastFrame = now;
        v = v with { PumpRpm = SteadyPump(v.PumpRpm, dt) };
        Ease(ref _cpuShown, v.CpuC, dt);
        Ease(ref _gpuShown, v.GpuC, dt);
        Ease(ref _pumpShown, v.PumpRpm, dt);
        UpdateMetrics(v, dt, now);
        var s = SlotsFor(screen, slots);

        using (var g = Graphics.FromImage(_bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Black);
            bool plainLighting = screen == "lighting";
            var bgStyle = UsesLighting(screen) && !style.On ? style with { Mode = "flow" } : style;
            _overBackground = bgStyle.On && !plainLighting;
            if (bgStyle.On)
            {
                Background(g, background);
                if (bgStyle.Mode == "bar") BarMask(g, bgStyle);
                if (_overBackground && bgStyle.Dim > 0)
                    using (var dim = new SolidBrush(Color.FromArgb((int)(Math.Clamp(bgStyle.Dim, 0, 0.9f) * 255), 0, 0, 0)))
                        g.FillRectangle(dim, 0, 0, P.Width, P.Height);
            }
            switch (screen)
            {
                case "speedometer": Speedometer(g, v, s); break;
                case "triple": Triple(g, v, s); break;
                case "chrono": Chrono(g, v, s); break;
                case "coolant": Coolant(g, v, s); break;
                case "twin": Twin(g, v, s); break;
                case "history": History(g, v, s); break;
                case "segments": Segments(g, v, s); break;
                case "load": Load(g, v, s); break;
                case "rings": Rings(g, v); break;
                case "clock": Clock(g, v); break;
                case "lighting": break;
                case "lighting-status": LightingStatus(g, v); break;
                default: Status(g, v); break;
            }
        }
        return Encode();
    }

    private float _pumpAvg = float.NaN;
    private int? _pumpHeld;

    /// <summary>
    /// The pump tachometer varies by about ±25 rpm from one second to the next.
    /// Shows a 3-second average, rounded to 10 rpm, that changes only after the average moves 25 rpm or more.
    /// </summary>
    private int? SteadyPump(int? rpm, float dt)
    {
        if (rpm is not int r) { _pumpAvg = float.NaN; return _pumpHeld = null; }
        _pumpAvg = float.IsNaN(_pumpAvg) ? r : _pumpAvg + (r - _pumpAvg) * (1 - MathF.Exp(-dt / 3));
        if (_pumpHeld is not int held || Math.Abs(_pumpAvg - held) >= 25) _pumpHeld = (int)MathF.Round(_pumpAvg / 10) * 10;
        return _pumpHeld;
    }

    /// <summary>Moves a shown value most of the way to its target within about half a second.</summary>
    private static void Ease(ref float shown, float? target, float dt)
    {
        if (target is not float t) return;
        if (float.IsNaN(shown)) { shown = t; return; }
        shown += (t - shown) * (1 - MathF.Exp(-dt * 6));
    }

    private void Status(Graphics g, LcdValues v)
    {
        // Arc gauge: 270 degrees, open at the bottom.
        var rect = new RectangleF(30, 30, P.Width - 60, P.Height - 60);
        using (var track = new Pen(Color.FromArgb(40, 40, 40), 22) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(track, rect, 135, 270);
        if (v.CpuC is float c)
        {
            float k = Math.Clamp((c - 30) / (95 - 30f), 0, 1);
            using var fill = new Pen(HeatColor(k), 22) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (k > 0) g.DrawArc(fill, rect, 135, 270 * k);
        }
        g.DrawString("CPU", _label, Brushes.Gray, new RectangleF(0, 110, P.Width, 40), _center);
        g.DrawString(v.CpuC is float t ? $"{t:F0}°" : "--", _big, Brushes.White, new RectangleF(0, 150, P.Width, 140), _center);
        g.DrawString($"GPU {(v.GpuC is float gt ? $"{gt:F0}°" : "--")}", _small, Brushes.LightGray, new RectangleF(0, 300, P.Width, 50), _center);
        g.DrawString($"Pump {(v.PumpRpm?.ToString() ?? "--")} rpm", _label, Brushes.Gray, new RectangleF(0, 355, P.Width, 40), _center);
    }

    /// <summary>Three concentric arc gauges: CPU (outer), GPU (middle), pump speed (inner).</summary>
    private void Rings(Graphics g, LcdValues v)
    {
        const float start = 135, sweep = 270;
        (string Label, float? Shown, float? Value, float K, float Radius, string Text)[] rings =
        [
            ("CPU", _cpuShown, v.CpuC, Temp01(_cpuShown), 205, v.CpuC is float c ? $"{c:F0}°" : "--"),
            ("GPU", _gpuShown, v.GpuC, Temp01(_gpuShown), 165, v.GpuC is float gt ? $"{gt:F0}°" : "--"),
            ("PUMP", _pumpShown, v.PumpRpm, float.IsNaN(_pumpShown) ? 0 : Math.Clamp(_pumpShown / PumpMaxRpm, 0, 1), 125, v.PumpRpm?.ToString() ?? "--"),
        ];
        foreach (var ring in rings)
        {
            var rect = new RectangleF(Cx - ring.Radius, Cy - ring.Radius, ring.Radius * 2, ring.Radius * 2);
            using (var track = new Pen(Color.FromArgb(38, 38, 44), 26) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(track, rect, start, sweep);
            if (ring.Value is not null && ring.K > 0.001f)
            {
                using var fill = new Pen(ring.Label == "PUMP" ? Color.FromArgb(60, 170, 255) : HeatColor(ring.K), 26) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(fill, rect, start, sweep * ring.K);
            }
            // Label at the open end of each ring, bottom left.
            float a = (start + sweep) * MathF.PI / 180;
            g.DrawString(ring.Label, _unit, Brushes.Gray, new RectangleF(Cx + ring.Radius * MathF.Cos(a) + 18, Cy + ring.Radius * MathF.Sin(a) - 14, 80, 28),
                new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center });
        }
        g.DrawString(rings[0].Text, _readout, Brushes.White, new RectangleF(0, Cy - 70, P.Width, 76), _center);
        g.DrawString($"GPU {rings[1].Text}", _unit, Brushes.LightGray, new RectangleF(0, Cy + 4, P.Width, 30), _center);
        g.DrawString($"{rings[2].Text} rpm", _unit, Brushes.Gray, new RectangleF(0, Cy + 36, P.Width, 30), _center);
    }

    private void Clock(Graphics g, LcdValues v)
    {
        var now = DateTime.Now;
        using (var ring = new Pen(Color.FromArgb(50, 50, 58), 6)) g.DrawEllipse(ring, Cx - 226, Cy - 226, 452, 452);
        // Seconds as a dot travelling round the rim.
        float a = (now.Second / 60f * 360 - 90) * MathF.PI / 180;
        using (var dot = new SolidBrush(Color.FromArgb(34, 211, 238))) g.FillEllipse(dot, Cx + 226 * MathF.Cos(a) - 9, Cy + 226 * MathF.Sin(a) - 9, 18, 18);
        g.DrawString(now.ToString("HH:mm"), _clock, Brushes.White, new RectangleF(0, Cy - 100, P.Width, 140), _center);
        g.DrawString(now.ToString("dddd d MMMM"), _clockSmall, Brushes.Gray, new RectangleF(0, Cy + 38, P.Width, 40), _center);
        g.DrawString($"CPU {(v.CpuC is float c ? $"{c:F0}°" : "--")}   GPU {(v.GpuC is float gt ? $"{gt:F0}°" : "--")}", _clockSmall, Brushes.LightGray,
            new RectangleF(0, Cy + 100, P.Width, 40), _center);
    }

    /// <summary>Scales the background to fill the screen with bilinear smoothing.</summary>
    private static void Background(Graphics g, LcdBackground? bg)
    {
        if (bg is null || bg.Width < 1 || bg.Height < 1 || bg.Rgb.Length < bg.Width * bg.Height * 3) return;
        using var src = new Bitmap(bg.Width, bg.Height, PixelFormat.Format24bppRgb);
        var data = src.LockBits(new Rectangle(0, 0, bg.Width, bg.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < bg.Height; y++)
            {
                for (int x = 0; x < bg.Width; x++)
                {
                    int i = (y * bg.Width + x) * 3;
                    row[x * 3] = bg.Rgb[i + 2];      // GDI+ stores 24-bit pixels as B, G, R
                    row[x * 3 + 1] = bg.Rgb[i + 1];
                    row[x * 3 + 2] = bg.Rgb[i];
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
            }
        }
        finally { src.UnlockBits(data); }
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe at the edges
        g.DrawImage(src, new Rectangle(0, 0, P.Width, P.Height), 0, 0, bg.Width, bg.Height, GraphicsUnit.Pixel, attrs);
        g.Restore(state);
    }

    /// <summary>
    /// Keeps a soft-edged band of the background and blacks out the rest. The band sweeps across once per period,
    /// left to right, or top to bottom when vertical, starting and ending fully off screen.
    /// </summary>
    private void BarMask(Graphics g, LcdBackgroundStyle s)
    {
        float span = s.Vertical ? P.Height : P.Width;
        float width = Math.Clamp(s.BarWidth, 0.05f, 1f) * span, feather = width * 0.35f;
        float period = Math.Max(0.3f, s.PeriodSeconds);
        float k = (float)(_clockSw.Elapsed.TotalSeconds % period / period);
        float centre = -width + k * (span + 2 * width);
        float a = centre - width / 2 - feather, b = centre - width / 2, c = centre + width / 2, d = centre + width / 2 + feather;
        // Black before a and after d; fades over a..b and c..d; the band b..c stays clear.
        // Antialiasing would leave a lit fringe along the frame edges, so the mask is drawn without it.
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        Black(g, s.Vertical, -2, a + 1);
        Fade(g, s.Vertical, a, b, Color.Black, Color.Transparent);
        Fade(g, s.Vertical, c, d, Color.Transparent, Color.Black);
        Black(g, s.Vertical, d - 1, span + 2);
        g.SmoothingMode = smoothing;
    }

    private static void Black(Graphics g, bool vertical, float from, float to)
    {
        if (to <= from) return;
        if (vertical) g.FillRectangle(Brushes.Black, -2, from, P.Width + 4, to - from);
        else g.FillRectangle(Brushes.Black, from, -2, to - from, P.Height + 4);
    }

    private static void Fade(Graphics g, bool vertical, float from, float to, Color c1, Color c2)
    {
        if (to - from < 1) return;
        var rect = vertical ? new RectangleF(-2, from, P.Width + 4, to - from) : new RectangleF(from, -2, to - from, P.Height + 4);
        using var brush = new LinearGradientBrush(rect, c1, c2, vertical ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal) { WrapMode = WrapMode.TileFlipX };
        g.FillRectangle(brush, rect);
    }

    private bool _overBackground;

    /// <summary>Temperatures on a dark disc over the lighting background.</summary>
    private void LightingStatus(Graphics g, LcdValues v)
    {
        using (var disc = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillEllipse(disc, Cx - 150, Cy - 150, 300, 300);
        g.DrawString("CPU", _unit, Brushes.LightGray, new RectangleF(0, Cy - 100, P.Width, 30), _center);
        g.DrawString(v.CpuC is float c ? $"{c:F0}°" : "--", _readout, Brushes.White, new RectangleF(0, Cy - 72, P.Width, 80), _center);
        g.DrawString($"GPU {(v.GpuC is float gt ? $"{gt:F0}°" : "--")}", _clockSmall, Brushes.White, new RectangleF(0, Cy + 14, P.Width, 40), _center);
        g.DrawString($"{(v.PumpRpm?.ToString() ?? "--")} rpm", _unit, Brushes.LightGray, new RectangleF(0, Cy + 58, P.Width, 30), _center);
    }

    private float Temp01(float t) => float.IsNaN(t) ? 0 : Math.Clamp((t - GaugeMin) / (GaugeMax - GaugeMin), 0, 1);

    /// <summary>Encodes any image file as a 480x480 frame, scaled to fill and centre-cropped.</summary>
    public byte[] FromFile(string path)
    {
        using var src = Image.FromFile(path);
        using (var g = Graphics.FromImage(_bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = Math.Max((float)P.Width / src.Width, (float)P.Height / src.Height);
            float w = src.Width * s, h = src.Height * s;
            g.Clear(Color.Black);
            g.DrawImage(src, (P.Width - w) / 2, (P.Height - h) / 2, w, h);
        }
        return Encode();
    }

    private byte[] Encode()
    {
        using var ms = new MemoryStream();
        _bmp.Save(ms, _jpeg, _quality);
        return ms.ToArray();
    }

    /// <summary>Blue at the cool end, through yellow, to red at the hot end.</summary>
    private static Color HeatColor(float k) =>
        k < 0.5f ? Lerp(Color.FromArgb(0, 120, 255), Color.FromArgb(255, 200, 0), k * 2)
                 : Lerp(Color.FromArgb(255, 200, 0), Color.FromArgb(255, 30, 0), (k - 0.5f) * 2);

    private static Color Lerp(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public void Dispose()
    {
        _bmp.Dispose();
        _quality.Dispose();
        _big.Dispose();
        _label.Dispose();
        _small.Dispose();
        _dialNum.Dispose();
        _stat.Dispose();
        _tag.Dispose();
        _readout.Dispose();
        _unit.Dispose();
        _clock.Dispose();
        _clockSmall.Dispose();
        _small16.Dispose();
        _tagSmall.Dispose();
        _center.Dispose();
        _centerOneLine.Dispose();
    }
}
