using System.Drawing;
using System.Drawing.Drawing2D;
using P = RgbBlaster.Drivers.Lcd.LinkLcdProtocol;

namespace RgbBlaster.Drivers.Lcd;

/// <summary>
/// One value a dial screen can show.
/// <paramref name="LabelDivisor"/> scales dial numbers, for example rpm shown in thousands.
/// </summary>
public sealed record LcdMetric(string Id, string Name, string Short, string ScaleNote, float Min, float Max, float? Red,
    float Minor, float Major, float LabelDivisor, string Format, Func<LcdValues, float?> Read)
{
    public string Text(float? v) => v is float x ? x.ToString(Format) + Suffix : "--";
    private string Suffix => ScaleNote switch { "°C" => "°", "%" => "%", _ => "" };
    public float Fraction(float v) => Math.Clamp((v - Min) / (Max - Min), 0, 1);
}

// Dial faces whose data slots the user chooses: see LcdDashboard.Metrics and LcdDashboard.DefaultSlots.
public sealed partial class LcdDashboard
{
    public static readonly IReadOnlyList<LcdMetric> Metrics =
    [
        new("cpu", "CPU temperature", "CPU", "°C", 20, 100, 85, 2, 10, 1, "0", v => v.CpuC),
        new("gpu", "GPU temperature", "GPU", "°C", 20, 100, 85, 2, 10, 1, "0", v => v.GpuC),
        new("liquid", "Liquid temperature", "LIQUID", "°C", 20, 60, 45, 1, 5, 1, "0.0", v => v.LiquidC),
        new("pump", "Pump speed", "PUMP", "rpm ×1000", 0, 3200, null, 250, 1000, 1000, "0", v => v.PumpRpm),
        new("cpuload", "CPU load", "CPU LOAD", "%", 0, 100, 90, 5, 20, 1, "0", v => v.CpuLoad),
        new("gpuload", "GPU load", "GPU LOAD", "%", 0, 100, 90, 5, 20, 1, "0", v => v.GpuLoad),
    ];

    /// <summary>Data slots of each screen that has them, in slot order. Screens without an entry show fixed data.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> DefaultSlots = new Dictionary<string, string[]>
    {
        ["speedometer"] = ["cpu", "gpu", "pump"],
        ["triple"] = ["cpu", "gpu", "pump"],
        ["chrono"] = ["cpu", "gpu", "pump"],
        ["coolant"] = ["liquid", "pump"],
        ["twin"] = ["cpu", "gpu", "liquid"],
        ["history"] = ["cpu", "gpu"],
        ["segments"] = ["cpu", "gpu", "liquid"],
        ["load"] = ["cpuload", "gpuload", "liquid"],
    };

    public static LcdMetric Metric(string id) => Metrics.FirstOrDefault(m => m.Id == id) ?? Metrics[0];

    /// <summary>The screen's slots: the chosen ids where valid, the defaults elsewhere.</summary>
    public static LcdMetric[] SlotsFor(string screen, IReadOnlyList<string>? chosen)
    {
        if (!DefaultSlots.TryGetValue(screen, out var def)) return [];
        return def.Select((d, i) => Metric(chosen is not null && i < chosen.Count && Metrics.Any(m => m.Id == chosen[i]) ? chosen[i] : d)).ToArray();
    }

    // Shown values ease toward the live ones; keyed by metric id.
    private readonly Dictionary<string, float> _shown = [];
    private float Shown(LcdMetric m) => _shown.TryGetValue(m.Id, out var v) ? v : float.NaN;

    // History for the history screen: one sample per second for each metric, last HistoryLength seconds.
    private const int HistoryLength = 300;
    private readonly Dictionary<string, Queue<float?>> _history = [];
    private double _lastSample = double.NegativeInfinity;

    private void UpdateMetrics(LcdValues v, float dt, double now)
    {
        foreach (var m in Metrics)
        {
            float s = Shown(m);
            Ease(ref s, m.Read(v), dt);
            _shown[m.Id] = s;
        }
        if (now - _lastSample < 1) return;
        _lastSample = now;
        foreach (var m in Metrics)
        {
            if (!_history.TryGetValue(m.Id, out var q))
            {
                q = new Queue<float?>();
                // Seed CPU and GPU from the service's history, so the chart is full at once.
                if (v.History is { } h && m.Id is "cpu" or "gpu") foreach (var p in h) q.Enqueue(m.Id == "cpu" ? p.Cpu : p.Gpu);
                _history[m.Id] = q;
            }
            q.Enqueue(m.Read(v));
            while (q.Count > HistoryLength) q.Dequeue();
        }
    }

    private const float DialStart = 150, DialSweep = 240, DialOuter = 222;
    private static float DialAngle(LcdMetric m, float v) => DialStart + DialSweep * m.Fraction(v);

    private static readonly Color NeedleA = Color.FromArgb(255, 70, 40), NeedleB = Color.FromArgb(235, 235, 240), NeedleC = Color.FromArgb(60, 170, 255);

    // ---------------------------------------------------------------- shared parts

    /// <summary>Metal bezel ring lit from the top left, shaded face and a shadow under the bezel.</summary>
    private void FaceDisc(Graphics g, float cx, float cy, float r)
    {
        int alpha = _overBackground ? 150 : 255;
        float bezel = Math.Max(4, r * 0.05f);
        var outer = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var ring = new GraphicsPath())
        {
            ring.AddEllipse(outer);
            ring.AddEllipse(cx - r + bezel, cy - r + bezel, (r - bezel) * 2, (r - bezel) * 2);
            using var metal = new LinearGradientBrush(outer, Color.FromArgb(205, 205, 215), Color.FromArgb(35, 35, 42), 45f);
            g.FillPath(metal, ring);
        }
        float fr = r - bezel;
        using (var face = new GraphicsPath())
        {
            face.AddEllipse(cx - fr, cy - fr, fr * 2, fr * 2);
            using var shade = new PathGradientBrush(face)
            {
                CenterPoint = new PointF(cx, cy - fr * 0.18f),
                CenterColor = Color.FromArgb(alpha, 40, 42, 50),
                SurroundColors = [Color.FromArgb(alpha, 6, 6, 8)],
            };
            g.FillPath(shade, face);
        }
        float sw = Math.Max(4, fr * 0.055f), sr = fr - sw / 2;
        using (var shadow = new Pen(Color.FromArgb(120, 0, 0, 0), sw)) g.DrawEllipse(shadow, cx - sr, cy - sr, sr * 2, sr * 2);
    }

    /// <summary>Ticks, red zone and numbers of <paramref name="m"/> on a 240-degree sweep around (cx, cy).</summary>
    private void Scale(Graphics g, float cx, float cy, float rOuter, LcdMetric m, Font numbers, bool numberEveryMajor = true)
    {
        float s = rOuter / DialOuter;
        if (m.Red is float red && red < m.Max)
        {
            float a0 = DialAngle(m, red);
            float rr = rOuter - 17 * s;
            using var redPen = new Pen(Color.FromArgb(220, 30, 30), 16 * s);
            g.DrawArc(redPen, cx - rr, cy - rr, rr * 2, rr * 2, a0, DialStart + DialSweep - a0);
        }
        using var minor = new Pen(Color.FromArgb(150, 150, 160), Math.Max(1.5f, 2 * s));
        using var major = new Pen(Color.White, Math.Max(2.5f, 5 * s));
        int steps = (int)Math.Round((m.Max - m.Min) / m.Minor);
        for (int i = 0; i <= steps; i++)
        {
            float v = m.Min + i * m.Minor;
            bool isMajor = Math.Abs(v / m.Major - MathF.Round(v / m.Major)) < 0.001f;
            float a = DialAngle(m, v) * MathF.PI / 180;
            float r1 = rOuter - (isMajor ? 40 : 24) * s, r2 = rOuter - 8 * s;
            g.DrawLine(isMajor ? major : minor, cx + r1 * MathF.Cos(a), cy + r1 * MathF.Sin(a), cx + r2 * MathF.Cos(a), cy + r2 * MathF.Sin(a));
            bool label = isMajor && (numberEveryMajor || v == m.Min || v == m.Max || Math.Abs(v - (m.Min + m.Max) / 2) < m.Minor / 2);
            if (!label) continue;
            float rn = rOuter - 70 * s;
            var text = (v / m.LabelDivisor).ToString("0.#");
            var brush = m.Red is float rl && v >= rl ? Brushes.OrangeRed : Brushes.White;
            float bw = Math.Max(68 * s, 44), bh = Math.Max(38 * s, 22);
            g.DrawString(text, numbers, brush, new RectangleF(cx + rn * MathF.Cos(a) - bw / 2, cy + rn * MathF.Sin(a) - bh / 2, bw, bh), _centerOneLine);
        }
    }

    /// <summary>Tapered needle with a flat tip, a glow in its own color and a dark edge; optional letter tag.</summary>
    private void Needle(Graphics g, float cx, float cy, float angleDeg, float length, float halfWidth, Color color, string? tag = null, float tagAt = 0)
    {
        float a = angleDeg * MathF.PI / 180;
        float ux = MathF.Cos(a), uy = MathF.Sin(a), px = -uy, py = ux;
        float tail = length * 0.125f, tip = halfWidth * 0.3f;
        PointF[] needle =
        [
            new(cx + ux * length + px * tip, cy + uy * length + py * tip),
            new(cx + px * halfWidth - ux * tail, cy + py * halfWidth - uy * tail),
            new(cx - px * halfWidth - ux * tail, cy - py * halfWidth - uy * tail),
            new(cx + ux * length - px * tip, cy + uy * length - py * tip),
        ];
        float glowScale = Math.Clamp(halfWidth / 17f, 0.4f, 1.2f);
        foreach (var (width, alpha) in new[] { (22f, 22), (15f, 38), (9f, 60), (5f, 90) })
            using (var glow = new Pen(Color.FromArgb(alpha, color), width * glowScale) { LineJoin = LineJoin.Round })
                g.DrawPolygon(glow, needle);
        using (var nb = new SolidBrush(color)) g.FillPolygon(nb, needle);
        using (var edge = new Pen(Color.FromArgb(160, 0, 0, 0), 1.5f) { LineJoin = LineJoin.Round }) g.DrawPolygon(edge, needle);
        if (tag is null) return;
        float tx = cx + ux * tagAt, ty = cy + uy * tagAt;
        using (var disc = new SolidBrush(Color.FromArgb(20, 20, 24))) g.FillEllipse(disc, tx - 14, ty - 14, 28, 28);
        using (var ring = new Pen(color, 3)) g.DrawEllipse(ring, tx - 14, ty - 14, 28, 28);
        g.DrawString(tag, _tag, Brushes.White, new RectangleF(tx - 14, ty - 13, 28, 28), _center);
    }

    /// <summary>Metal hub cap lit from the top left.</summary>
    private static void HubCap(Graphics g, float cx, float cy, float r)
    {
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            using var cap = new PathGradientBrush(path)
            {
                CenterPoint = new PointF(cx - r * 0.3f, cy - r * 0.3f),
                CenterColor = Color.FromArgb(175, 175, 185),
                SurroundColors = [Color.FromArgb(28, 28, 34)],
            };
            g.FillPath(cap, path);
        }
        using var ring = new Pen(Color.FromArgb(90, 90, 100), 2);
        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
    }

    /// <summary>Big center hub, then the glass reflection over the whole dial.</summary>
    private static void Hub(Graphics g)
    {
        HubCap(g, Cx, Cy, 22);
        Glass(g);
    }

    /// <summary>Faint reflection on the upper half of the dial glass, fading downward.</summary>
    private static void Glass(Graphics g)
    {
        var state = g.Save();
        using (var clip = new GraphicsPath())
        {
            clip.AddEllipse(Cx - 226, Cy - 226, 452, 452);
            g.SetClip(clip);
        }
        var shine = new RectangleF(Cx - 200, Cy - 236, 400, 250);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(shine);
            using var brush = new LinearGradientBrush(shine, Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
            g.FillPath(brush, path);
        }
        g.Restore(state);
    }

    /// <summary>Up to three slots in equal columns across the bottom of a dial.</summary>
    private void StatusRow(Graphics g, LcdValues v, IReadOnlyList<LcdMetric> cols, float top = Cy + 102)
    {
        const float colW = 116;
        float x0 = Cx - colW * cols.Count / 2;
        for (int i = 0; i < cols.Count; i++)
        {
            g.DrawString(cols[i].Short, _unit, Brushes.Gray, new RectangleF(x0 + i * colW - 10, top, colW + 20, 24), _center);
            g.DrawString(cols[i].Text(cols[i].Read(v)), _stat, Brushes.White, new RectangleF(x0 + i * colW, top + 20, colW, 50), _centerOneLine);
        }
    }

    // ---------------------------------------------------------------- faces

    /// <summary>Car-style dial for slot 0 with the slots in a row below.</summary>
    private void Speedometer(Graphics g, LcdValues v, LcdMetric[] s)
    {
        FaceDisc(g, Cx, Cy, 239);
        Scale(g, Cx, Cy, DialOuter, s[0], _dialNum);
        StatusRow(g, v, s);
        if (!float.IsNaN(Shown(s[0]))) Needle(g, Cx, Cy, DialAngle(s[0], Shown(s[0])), 190, 17.5f, NeedleA);
        Hub(g);
    }

    /// <summary>
    /// One dial, three needles. Slots 0 and 1 read the outer scale (slot 0's range); slot 2 reads the inner scale.
    /// Each needle carries a letter, so the needles can be told apart without color.
    /// </summary>
    private void Triple(Graphics g, LcdValues v, LcdMetric[] s)
    {
        FaceDisc(g, Cx, Cy, 239);
        Scale(g, Cx, Cy, DialOuter, s[0], _dialNum);
        const float rIn = 118;
        var inner = s[2];
        using (var arc = new Pen(Color.FromArgb(90, 90, 100), 3)) g.DrawArc(arc, Cx - rIn, Cy - rIn, rIn * 2, rIn * 2, DialStart, DialSweep);
        using (var tick = new Pen(Color.FromArgb(170, 170, 180), 3))
        {
            int steps = (int)Math.Round((inner.Max - inner.Min) / (inner.Major / 2));
            for (int i = 0; i <= steps; i++)
            {
                float val = inner.Min + i * inner.Major / 2;
                bool major = i % 2 == 0;
                float a = DialAngle(inner, val) * MathF.PI / 180, r1 = rIn - (major ? 14 : 8);
                g.DrawLine(tick, Cx + r1 * MathF.Cos(a), Cy + r1 * MathF.Sin(a), Cx + rIn * MathF.Cos(a), Cy + rIn * MathF.Sin(a));
                if (major)
                    g.DrawString((val / inner.LabelDivisor).ToString("0.#"), _unit, Brushes.LightGray,
                        new RectangleF(Cx + (rIn - 32) * MathF.Cos(a) - 22, Cy + (rIn - 32) * MathF.Sin(a) - 13, 44, 26), _center);
            }
        }
        g.DrawString($"{inner.Short.ToLowerInvariant()} {inner.ScaleNote}", _unit, Brushes.Gray, new RectangleF(0, Cy + 34, P.Width, 26), _center);
        StatusRow(g, v, s);

        // A needle on the outer scale uses its own metric's range, so a different unit still spans the dial.
        if (!float.IsNaN(Shown(inner))) Needle(g, Cx, Cy, DialAngle(inner, Shown(inner)), 104, 15, NeedleC, Letter(inner), 60);
        if (!float.IsNaN(Shown(s[1]))) Needle(g, Cx, Cy, DialAngle(s[1], Shown(s[1])), 192, 15, NeedleB, Letter(s[1]), 100);
        if (!float.IsNaN(Shown(s[0]))) Needle(g, Cx, Cy, DialAngle(s[0], Shown(s[0])), 192, 17.5f, NeedleA, Letter(s[0]), 136);
        Hub(g);
    }

    private static string Letter(LcdMetric m) => m.Id switch { "cpu" => "C", "gpu" => "G", "liquid" => "L", "pump" => "P", "cpuload" => "c", "gpuload" => "g", _ => "?" };

    /// <summary>Watch face: hour, minute and second hands, a date window and three small dials for the slots.</summary>
    private void Chrono(Graphics g, LcdValues v, LcdMetric[] s)
    {
        FaceDisc(g, Cx, Cy, 239);
        // Hour markers; the ones under the small dials are left out.
        using (var bar = new Pen(Color.White, 7))
        using (var thin = new Pen(Color.FromArgb(170, 170, 180), 3))
            for (int h = 0; h < 12; h++)
            {
                if (h is 3 or 6 or 9) continue;
                float a = (h * 30 - 90) * MathF.PI / 180;
                bool big = h == 0;
                float r1 = big ? 172 : 190, r2 = 214;
                g.DrawLine(big || h % 3 == 0 ? bar : thin, Cx + r1 * MathF.Cos(a), Cy + r1 * MathF.Sin(a), Cx + r2 * MathF.Cos(a), Cy + r2 * MathF.Sin(a));
            }
        using (var minuteTick = new Pen(Color.FromArgb(110, 110, 120), 1.5f))
            for (int m = 0; m < 60; m++)
            {
                if (m % 5 == 0) continue;
                float a = (m * 6 - 90) * MathF.PI / 180;
                g.DrawLine(minuteTick, Cx + 206 * MathF.Cos(a), Cy + 206 * MathF.Sin(a), Cx + 214 * MathF.Cos(a), Cy + 214 * MathF.Sin(a));
            }

        // Small dials at 9, 3 and 6 o'clock.
        (float X, float Y)[] at = [(Cx - 108, Cy), (Cx + 108, Cy), (Cx, Cy + 108)];
        for (int i = 0; i < 3; i++) SmallDial(g, at[i].X, at[i].Y, 66, s[i], v);

        // Date window at 12 o'clock.
        var now = DateTime.Now;
        var win = new RectangleF(Cx - 42, Cy - 132, 84, 36);
        using (var wb = new SolidBrush(Color.FromArgb(235, 235, 230))) g.FillRectangle(wb, win);
        using (var wp = new Pen(Color.FromArgb(90, 90, 100), 2)) g.DrawRectangle(wp, win.X, win.Y, win.Width, win.Height);
        using (var dateBrush = new SolidBrush(Color.FromArgb(20, 20, 24)))
            g.DrawString(now.ToString("ddd d").ToUpperInvariant(), _unit, dateBrush, win, _center);

        // Hands.
        float hour = (now.Hour % 12 + now.Minute / 60f) * 30 - 90, minute = (now.Minute + now.Second / 60f) * 6 - 90, second = now.Second * 6 - 90;
        Needle(g, Cx, Cy, hour, 120, 11, Color.FromArgb(240, 240, 240));
        Needle(g, Cx, Cy, minute, 190, 9, Color.FromArgb(240, 240, 240));
        Needle(g, Cx, Cy, second, 205, 3, Color.FromArgb(255, 60, 40));
        HubCap(g, Cx, Cy, 14);
        Glass(g);
    }

    /// <summary>Small dial for one slot: scale with min, middle and max numbers, a needle, the value and its label.</summary>
    private void SmallDial(Graphics g, float cx, float cy, float r, LcdMetric m, LcdValues v)
    {
        using (var rim = new Pen(Color.FromArgb(120, 120, 130), 2)) g.DrawEllipse(rim, cx - r, cy - r, r * 2, r * 2);
        using (var inner = new SolidBrush(Color.FromArgb(_overBackground ? 110 : 200, 10, 10, 12))) g.FillEllipse(inner, cx - r + 2, cy - r + 2, r * 2 - 4, r * 2 - 4);
        Scale(g, cx, cy, r - 4, m, _small16, numberEveryMajor: false);
        g.DrawString(m.Short, _tagSmall, Brushes.Gray, new RectangleF(cx - r, cy + r * 0.14f, r * 2, 18), _center);
        g.DrawString(m.Text(m.Read(v)), _tag, Brushes.White, new RectangleF(cx - r, cy + r * 0.42f, r * 2, 24), _centerOneLine);
        if (!float.IsNaN(Shown(m))) Needle(g, cx, cy, DialAngle(m, Shown(m)), r - 12, 5, NeedleA);
        HubCap(g, cx, cy, 6);
    }

    /// <summary>Single large dial for slot 0, with its value in large digits and slot 1 below.</summary>
    private void Coolant(Graphics g, LcdValues v, LcdMetric[] s)
    {
        FaceDisc(g, Cx, Cy, 239);
        Scale(g, Cx, Cy, DialOuter, s[0], _dialNum);
        g.DrawString(s[0].Short, _unit, Brushes.Gray, new RectangleF(0, Cy + 62, P.Width, 26), _center);
        g.DrawString(s[0].Text(s[0].Read(v)), _readout, Brushes.White, new RectangleF(0, Cy + 84, P.Width, 72), _centerOneLine);
        g.DrawString($"{s[1].Short}  {s[1].Text(s[1].Read(v))}", _unit, Brushes.LightGray, new RectangleF(0, Cy + 158, P.Width, 28), _center);
        if (!float.IsNaN(Shown(s[0]))) Needle(g, Cx, Cy, DialAngle(s[0], Shown(s[0])), 190, 17.5f, NeedleC);
        Hub(g);
    }

    /// <summary>Two dials side by side, like a motorcycle cluster, with slot 2 below.</summary>
    private void Twin(Graphics g, LcdValues v, LcdMetric[] s)
    {
        FaceDisc(g, Cx, Cy, 239);
        (float X, LcdMetric M, Color C)[] dials = [(Cx - 106, s[0], NeedleA), (Cx + 106, s[1], NeedleB)];
        const float r = 100, cy = Cy - 22;
        foreach (var (x, m, c) in dials)
        {
            using (var rim = new Pen(Color.FromArgb(150, 150, 160), 3)) g.DrawEllipse(rim, x - r, cy - r, r * 2, r * 2);
            using (var face = new SolidBrush(Color.FromArgb(_overBackground ? 110 : 200, 10, 10, 12))) g.FillEllipse(face, x - r + 2, cy - r + 2, r * 2 - 4, r * 2 - 4);
            Scale(g, x, cy, r - 4, m, _small16, numberEveryMajor: false);
            if (!float.IsNaN(Shown(m))) Needle(g, x, cy, DialAngle(m, Shown(m)), r - 14, 8, c);
            HubCap(g, x, cy, 9);
            g.DrawString(m.Short, _unit, Brushes.Gray, new RectangleF(x - 70, cy + r + 6, 140, 26), _center);
            g.DrawString(m.Text(m.Read(v)), _stat, Brushes.White, new RectangleF(x - 80, cy + r + 28, 160, 50), _centerOneLine);
        }
        g.DrawString($"{s[2].Short}  {s[2].Text(s[2].Read(v))}", _unit, Brushes.LightGray, new RectangleF(0, Cy + 168, P.Width, 28), _center);
        Glass(g);
    }

    /// <summary>
    /// The last five minutes as lines round the rim: slot 0 solid, slot 1 dashed, oldest at the lower left.
    /// The scale is slot 0's range; slot 1 is drawn on its own range.
    /// </summary>
    private void History(Graphics g, LcdValues v, LcdMetric[] s)
    {
        FaceDisc(g, Cx, Cy, 239);
        const float start = 120, sweep = 300, rLo = 112, rHi = 212;
        // Grid: rings at each major step of slot 0, with their values at the top.
        using (var grid = new Pen(Color.FromArgb(60, 60, 70), 1.5f))
            for (int i = 0; s[0].Min + i * s[0].Major <= s[0].Max + 0.01f; i++)
            {
                float val = s[0].Min + i * s[0].Major;
                float rr = rLo + (rHi - rLo) * s[0].Fraction(val);
                g.DrawArc(grid, Cx - rr, Cy - rr, rr * 2, rr * 2, start, sweep);
                // Every second ring is numbered, so the numbers do not touch.
                if (i % 2 == 0) g.DrawString((val / s[0].LabelDivisor).ToString("0.#"), _tagSmall, Brushes.Gray, new RectangleF(Cx - 20, Cy - rr - 9, 40, 18), _center);
            }
        DrawTrace(g, s[1], start, sweep, rLo, rHi, NeedleB, dashed: true);
        DrawTrace(g, s[0], start, sweep, rLo, rHi, NeedleA, dashed: false);

        g.DrawString(s[0].Short, _unit, Brushes.Gray, new RectangleF(0, Cy - 66, P.Width, 26), _center);
        g.DrawString(s[0].Text(s[0].Read(v)), _readout, Brushes.White, new RectangleF(0, Cy - 44, P.Width, 72), _centerOneLine);
        g.DrawString($"{s[1].Short} {s[1].Text(s[1].Read(v))}", _clockSmall, Brushes.LightGray, new RectangleF(0, Cy + 30, P.Width, 36), _center);
        // Legend: line styles, since the colors may not be told apart.
        using (var solid = new Pen(NeedleA, 4)) g.DrawLine(solid, Cx - 70, Cy + 82, Cx - 40, Cy + 82);
        using (var dash = new Pen(NeedleB, 4) { DashStyle = DashStyle.Dash }) g.DrawLine(dash, Cx + 10, Cy + 82, Cx + 40, Cy + 82);
        g.DrawString(s[0].Short, _tagSmall, Brushes.Gray, new RectangleF(Cx - 36, Cy + 73, 46, 18), _center);
        g.DrawString(s[1].Short, _tagSmall, Brushes.Gray, new RectangleF(Cx + 42, Cy + 73, 56, 18), _center);
        g.DrawString("last 5 min", _tagSmall, Brushes.Gray, new RectangleF(0, Cy + 100, P.Width, 18), _center);
        Glass(g);
    }

    private void DrawTrace(Graphics g, LcdMetric m, float start, float sweep, float rLo, float rHi, Color color, bool dashed)
    {
        if (!_history.TryGetValue(m.Id, out var q) || q.Count < 2) return;
        var samples = q.ToArray();
        var pts = new List<PointF>();
        using var pen = new Pen(color, dashed ? 3.5f : 5) { LineJoin = LineJoin.Round, DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
        void Flush() { if (pts.Count > 1) g.DrawLines(pen, pts.ToArray()); pts.Clear(); }
        for (int i = 0; i < samples.Length; i++)
        {
            // Oldest sample sits where the full history would start, so a short history grows from the right end.
            float k = (HistoryLength - samples.Length + i) / (float)(HistoryLength - 1);
            if (samples[i] is not float val) { Flush(); continue; }
            float a = (start + sweep * k) * MathF.PI / 180, rr = rLo + (rHi - rLo) * m.Fraction(val);
            pts.Add(new PointF(Cx + rr * MathF.Cos(a), Cy + rr * MathF.Sin(a)));
        }
        Flush();
    }

    /// <summary>LED-style segment rings: slot 0 outside, slot 1 inside, big digits in the middle.</summary>
    private void Segments(Graphics g, LcdValues v, LcdMetric[] s)
    {
        if (_overBackground)
            using (var disc = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillEllipse(disc, 0, 0, P.Width, P.Height);
        SegmentRing(g, s[0], v, 196, 30, 40);
        SegmentRing(g, s[1], v, 152, 22, 32);
        g.DrawString(s[0].Short, _unit, Brushes.Gray, new RectangleF(0, Cy - 96, P.Width, 26), _center);
        g.DrawString(s[0].Text(s[0].Read(v)), _clock, Brushes.White, new RectangleF(0, Cy - 60, P.Width, 120), _centerOneLine);
        g.DrawString($"{s[1].Short} {s[1].Text(s[1].Read(v))}", _clockSmall, Brushes.LightGray, new RectangleF(0, Cy + 56, P.Width, 36), _center);
        g.DrawString($"{s[2].Short} {s[2].Text(s[2].Read(v))}", _unit, Brushes.Gray, new RectangleF(0, Cy + 94, P.Width, 28), _center);
    }

    private static void SegmentRing(Graphics g, LcdMetric m, LcdValues v, float r, float thickness, int count)
    {
        const float start = 135, sweep = 270;
        float k = m.Read(v) is float val ? m.Fraction(val) : 0;
        int lit = (int)MathF.Round(k * count);
        float step = sweep / count, gap = step * 0.25f;
        var rect = new RectangleF(Cx - r, Cy - r, r * 2, r * 2);
        for (int i = 0; i < count; i++)
        {
            var c = i < lit ? HeatColor(i / (float)(count - 1)) : Color.FromArgb(34, 34, 40);
            using var pen = new Pen(c, thickness);
            g.DrawArc(pen, rect, start + i * step, step - gap);
        }
    }

    /// <summary>Two half-ring bars: slot 0 on the left, slot 1 on the right, each filling upward; slot 2 at the bottom.</summary>
    private void Load(Graphics g, LcdValues v, LcdMetric[] s)
    {
        FaceDisc(g, Cx, Cy, 239);
        const float r = 196, w = 26;
        var rect = new RectangleF(Cx - r, Cy - r, r * 2, r * 2);
        using (var track = new Pen(Color.FromArgb(38, 38, 44), w)) { g.DrawArc(track, rect, 100, 160); g.DrawArc(track, rect, 80, -160); }
        if (!float.IsNaN(Shown(s[0])))
            using (var fill = new Pen(HeatColor(s[0].Fraction(Shown(s[0]))), w)) g.DrawArc(fill, rect, 100, 160 * s[0].Fraction(Shown(s[0])));
        if (!float.IsNaN(Shown(s[1])))
            using (var fill = new Pen(HeatColor(s[1].Fraction(Shown(s[1]))), w)) g.DrawArc(fill, rect, 80, -160 * s[1].Fraction(Shown(s[1])));
        using (var divider = new Pen(Color.FromArgb(60, 60, 70), 2)) g.DrawLine(divider, Cx, Cy - 120, Cx, Cy + 80);
        for (int i = 0; i < 2; i++)
        {
            float x = i == 0 ? Cx - 170 : Cx + 10;
            g.DrawString(s[i].Short, _unit, Brushes.Gray, new RectangleF(x, Cy - 70, 160, 26), _center);
            g.DrawString(s[i].Text(s[i].Read(v)), _readout, Brushes.White, new RectangleF(x - 10, Cy - 46, 180, 72), _centerOneLine);
        }
        g.DrawString($"{s[2].Short}  {s[2].Text(s[2].Read(v))}", _clockSmall, Brushes.LightGray, new RectangleF(0, Cy + 110, P.Width, 36), _center);
        Glass(g);
    }

    /// <summary>Draws CPU, GPU and liquid temperatures in a band near the bottom of a media frame.</summary>
    public byte[] Overlay(byte[] jpeg, LcdValues v)
    {
        using (var ms = new MemoryStream(jpeg))
        using (var src = Image.FromStream(ms))
        using (var g = Graphics.FromImage(_bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawImage(src, 0, 0, P.Width, P.Height);
            var band = new RectangleF(Cx - 180, P.Height - 118, 360, 48);
            using (var path = RoundRect(band, 24))
            using (var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                g.FillPath(bg, path);
            string text = $"CPU {Metric("cpu").Text(v.CpuC)}   GPU {Metric("gpu").Text(v.GpuC)}" + (v.LiquidC is float l ? $"   LIQUID {l:0.0}°" : "");
            g.DrawString(text, _unit, Brushes.White, band, _centerOneLine);
        }
        return Encode();
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
