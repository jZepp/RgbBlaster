using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using P = RgbBlaster.Drivers.Lcd.LinkLcdProtocol;

namespace RgbBlaster.Drivers.Lcd;

/// <summary>Live values for the built-in screens. Null means not available.</summary>
public readonly record struct LcdValues(float? CpuC, float? GpuC, int? PumpRpm);

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
public sealed class LcdDashboard : IDisposable
{
    /// <summary>Built-in screens and the interval between their frames. Lighting screens need a background.</summary>
    public static readonly IReadOnlyList<(string Id, string Name, int FrameMs, bool UsesLighting)> Screens =
    [
        ("status", "Status", 1000, false),
        ("speedometer", "Speedometer", 100, false),
        ("triple", "Triple dial", 100, false),
        ("rings", "Rings", 100, false),
        ("clock", "Clock", 1000, false),
        ("lighting", "Lighting", 66, true),
        ("lighting-status", "Lighting + status", 66, true),
    ];

    /// <summary>Background size requested for lighting screens; the screen scales it up smoothly.</summary>
    public const int BackgroundSize = 64;

    private const float Cx = P.Width / 2f, Cy = P.Height / 2f;
    private const string Face = "Bahnschrift";

    private readonly Bitmap _bmp = new(P.Width, P.Height, PixelFormat.Format24bppRgb);
    private readonly ImageCodecInfo _jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
    private readonly EncoderParameters _quality = new(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 85L) } };
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
    private readonly StringFormat _center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

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
    public byte[] Render(string screen, LcdValues v, LcdBackground? background, LcdBackgroundStyle style)
    {
        double now = _clockSw.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastFrame, 0, 1);
        _lastFrame = now;
        Ease(ref _cpuShown, v.CpuC, dt);
        Ease(ref _gpuShown, v.GpuC, dt);
        Ease(ref _pumpShown, v.PumpRpm, dt);

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
                case "speedometer": Speedometer(g, v); break;
                case "triple": Triple(g, v); break;
                case "rings": Rings(g, v); break;
                case "clock": Clock(g, v); break;
                case "lighting": break;
                case "lighting-status": LightingStatus(g, v); break;
                default: Status(g, v); break;
            }
        }
        return Encode();
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

    private const float DialStart = 150, DialSweep = 240, DialOuter = 222;
    private float DialAngle(float temp) => DialStart + DialSweep * Math.Clamp((temp - GaugeMin) / (GaugeMax - GaugeMin), 0, 1);

    /// <summary>Car-style dial: CPU temperature on a 240-degree sweep with a needle, numbered ticks and a red zone.</summary>
    private void Speedometer(Graphics g, LcdValues v)
    {
        DialFace(g);
        StatusRow(g, v);
        if (!float.IsNaN(_cpuShown)) Needle(g, DialAngle(_cpuShown), 190, 7, Color.FromArgb(255, 70, 40), null, 0);
        Hub(g);
    }

    /// <summary>
    /// One dial, three needles. CPU and GPU read the outer temperature scale; the pump reads the inner rpm scale.
    /// Each needle carries a letter, so the needles can be told apart without colour.
    /// </summary>
    private void Triple(Graphics g, LcdValues v)
    {
        DialFace(g);
        // Inner pump scale, 0..PumpMaxRpm on the same sweep, numbered in thousands.
        const float rIn = 118;
        float PumpAngle(float rpm) => DialStart + DialSweep * Math.Clamp(rpm / PumpMaxRpm, 0, 1);
        using (var arc = new Pen(Color.FromArgb(90, 90, 100), 3)) g.DrawArc(arc, Cx - rIn, Cy - rIn, rIn * 2, rIn * 2, DialStart, DialSweep);
        using (var tick = new Pen(Color.FromArgb(170, 170, 180), 3))
            for (int rpm = 0; rpm <= PumpMaxRpm; rpm += 500)
            {
                bool major = rpm % 1000 == 0;
                float a = PumpAngle(rpm) * MathF.PI / 180, r1 = rIn - (major ? 14 : 8);
                g.DrawLine(tick, Cx + r1 * MathF.Cos(a), Cy + r1 * MathF.Sin(a), Cx + rIn * MathF.Cos(a), Cy + rIn * MathF.Sin(a));
                if (major)
                    g.DrawString((rpm / 1000).ToString(), _unit, Brushes.LightGray,
                        new RectangleF(Cx + (rIn - 32) * MathF.Cos(a) - 20, Cy + (rIn - 32) * MathF.Sin(a) - 13, 40, 26), _center);
            }
        g.DrawString("rpm ×1000", _unit, Brushes.Gray, new RectangleF(0, Cy + 34, P.Width, 26), _center);
        StatusRow(g, v);

        // Shortest needle at the bottom of the stack.
        if (!float.IsNaN(_pumpShown)) Needle(g, PumpAngle(_pumpShown), 104, 6, Color.FromArgb(60, 170, 255), "P", 60);
        if (!float.IsNaN(_gpuShown)) Needle(g, DialAngle(_gpuShown), 160, 6, Color.FromArgb(235, 235, 240), "G", 100);
        if (!float.IsNaN(_cpuShown)) Needle(g, DialAngle(_cpuShown), 192, 7, Color.FromArgb(255, 70, 40), "C", 136);
        Hub(g);
    }

    /// <summary>CPU, GPU and pump in three equal columns across the bottom of a dial.</summary>
    private void StatusRow(Graphics g, LcdValues v)
    {
        (string Label, string Value)[] cols =
        [
            ("CPU", v.CpuC is float c ? $"{c:F0}°" : "--"),
            ("GPU", v.GpuC is float gt ? $"{gt:F0}°" : "--"),
            ("PUMP", v.PumpRpm?.ToString() ?? "--"),
        ];
        const float colW = 106, top = Cy + 102;
        float x0 = Cx - colW * cols.Length / 2;
        for (int i = 0; i < cols.Length; i++)
        {
            g.DrawString(cols[i].Label, _unit, Brushes.Gray, new RectangleF(x0 + i * colW, top, colW, 24), _center);
            g.DrawString(cols[i].Value, _stat, Brushes.White, new RectangleF(x0 + i * colW, top + 20, colW, 50), _center);
        }
    }

    /// <summary>Tapered needle from behind the hub to <paramref name="length"/>, with an optional letter tag at <paramref name="tagAt"/>.</summary>
    private void Needle(Graphics g, float angleDeg, float length, float halfWidth, Color color, string? tag, float tagAt)
    {
        float a = angleDeg * MathF.PI / 180;
        float ux = MathF.Cos(a), uy = MathF.Sin(a), px = -uy, py = ux;
        PointF[] needle =
        [
            new(Cx + ux * length, Cy + uy * length),
            new(Cx + px * halfWidth - ux * 24, Cy + py * halfWidth - uy * 24),
            new(Cx - px * halfWidth - ux * 24, Cy - py * halfWidth - uy * 24),
        ];
        using (var nb = new SolidBrush(color)) g.FillPolygon(nb, needle);
        if (tag is null) return;
        float tx = Cx + ux * tagAt, ty = Cy + uy * tagAt;
        using (var disc = new SolidBrush(Color.FromArgb(20, 20, 24))) g.FillEllipse(disc, tx - 14, ty - 14, 28, 28);
        using (var ring = new Pen(color, 3)) g.DrawEllipse(ring, tx - 14, ty - 14, 28, 28);
        g.DrawString(tag, _tag, Brushes.White, new RectangleF(tx - 14, ty - 13, 28, 28), _center);
    }

    private static void Hub(Graphics g)
    {
        using (var hub = new SolidBrush(Color.FromArgb(40, 40, 46))) g.FillEllipse(hub, Cx - 22, Cy - 22, 44, 44);
        using (var hubRing = new Pen(Color.FromArgb(120, 120, 130), 3)) g.DrawEllipse(hubRing, Cx - 22, Cy - 22, 44, 44);
    }

    /// <summary>Bezel, face, red zone, ticks and numbers of the temperature dial.</summary>
    private void DialFace(Graphics g)
    {
        const float start = DialStart, sweep = DialSweep, rOuter = DialOuter;
        float Angle(float temp) => DialAngle(temp);

        // Bezel rings.
        using (var bezel = new Pen(Color.FromArgb(70, 70, 78), 4)) g.DrawEllipse(bezel, Cx - 234, Cy - 234, 468, 468);
        // Over a background the face is part see-through, so the lighting shows behind the dial.
        using (var face = new SolidBrush(Color.FromArgb(_overBackground ? 150 : 255, 14, 14, 18))) g.FillEllipse(face, Cx - 230, Cy - 230, 460, 460);

        // Red zone: a solid band on the outer edge.
        float redStart = Angle(RedLine);
        using (var red = new Pen(Color.FromArgb(220, 30, 30), 16)) g.DrawArc(red, Cx - 205, Cy - 205, 410, 410, redStart, start + sweep - redStart);

        // Ticks every 2 °C, long ticks and numbers every 10 °C.
        using var minor = new Pen(Color.FromArgb(150, 150, 160), 2);
        using var major = new Pen(Color.White, 5);
        for (int temp = (int)GaugeMin; temp <= GaugeMax; temp += 2)
        {
            bool isMajor = temp % 10 == 0;
            float a = Angle(temp) * MathF.PI / 180;
            float r1 = isMajor ? rOuter - 40 : rOuter - 24, r2 = rOuter - 8;
            g.DrawLine(isMajor ? major : minor, Cx + r1 * MathF.Cos(a), Cy + r1 * MathF.Sin(a), Cx + r2 * MathF.Cos(a), Cy + r2 * MathF.Sin(a));
            if (isMajor)
            {
                float rn = rOuter - 70;
                g.DrawString(temp.ToString(), _dialNum, temp >= RedLine ? Brushes.OrangeRed : Brushes.White,
                    new RectangleF(Cx + rn * MathF.Cos(a) - 34, Cy + rn * MathF.Sin(a) - 19, 68, 38), _center);
            }
        }
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
        _center.Dispose();
    }
}
