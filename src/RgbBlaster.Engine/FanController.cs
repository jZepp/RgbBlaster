using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>
/// Applies temperature curves to fan channels. Call <see cref="Tick"/> about once per second from one thread.
/// Each curve reads its hottest sensor, looks up the target duty, and moves toward it at the configured rise and fall
/// rates. With no reading, the curve goes straight to its failsafe duty.
/// </summary>
public sealed class FanController
{
    private sealed class Curve(FanCurveConfig cfg, IFanChannel[] fans)
    {
        public FanCurveConfig Cfg { get; } = cfg;
        public IFanChannel[] Fans { get; } = fans;
        public float? Duty;
        public byte? Sent;
        public float? LastTemp;
    }

    private readonly List<Curve> _curves = [];
    private readonly ITemperatureSource _temps;

    public FanController(IEnumerable<FanCurveConfig> curves, IEnumerable<IFanChannel> fans, ITemperatureSource temps)
    {
        _temps = temps;
        var byId = fans.GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in curves)
        {
            if (c.Points.Count == 0) { Warnings.Add($"Curve '{c.Name}' has no points; skipped."); continue; }
            var list = new List<IFanChannel>();
            foreach (var id in c.Fans)
            {
                if (!byId.TryGetValue(id, out var f)) { Warnings.Add($"Curve '{c.Name}': fan '{id}' not found."); continue; }
                if (!used.Add(id)) { Warnings.Add($"Curve '{c.Name}': fan '{id}' is already in another curve; skipped."); continue; }
                list.Add(f);
            }
            if (list.Count > 0) _curves.Add(new Curve(c, list.ToArray()));
        }
    }

    public List<string> Warnings { get; } = [];

    /// <param name="dtSeconds">Seconds since the previous tick; limits how far each duty moves.</param>
    public void Tick(double dtSeconds)
    {
        foreach (var c in _curves)
        {
            float? temp = null;
            foreach (var s in c.Cfg.Sensors)
                if (_temps.ReadCelsius(s) is float t && !float.IsNaN(t))
                    temp = temp is null ? t : Math.Max(temp.Value, t);
            c.LastTemp = temp;

            float duty;
            if (temp is null)
                duty = c.Cfg.FailsafePercent;
            else
            {
                float target = Interpolate(c.Cfg.Points, temp.Value);
                if (c.Duty is not float cur) duty = target;
                else
                {
                    float up = (float)(c.Cfg.RisePercentPerSecond * dtSeconds), down = (float)(c.Cfg.FallPercentPerSecond * dtSeconds);
                    duty = Math.Clamp(target, cur - down, cur + up);
                }
            }
            c.Duty = duty;

            byte send = (byte)Math.Clamp(MathF.Round(duty), 0, 100);
            if (send == c.Sent) continue;
            foreach (var f in c.Fans) f.SetDutyPercent(send);
            c.Sent = send;
        }
    }

    /// <summary>One line per curve: name, temperature, duty.</summary>
    public IEnumerable<string> Status() =>
        _curves.Select(c => $"{c.Cfg.Name} {(c.LastTemp is float t ? $"{t:F0}C" : "no temp")} {c.Sent?.ToString() ?? "-"}%");

    public static float Interpolate(IReadOnlyList<FanCurvePoint> points, float temp)
    {
        var p = points.OrderBy(x => x.Temp).ToList();
        if (temp <= p[0].Temp) return p[0].Duty;
        for (int i = 1; i < p.Count; i++)
            if (temp <= p[i].Temp)
            {
                if (p[i].Temp == p[i - 1].Temp) return p[i].Duty;
                float k = (temp - p[i - 1].Temp) / (p[i].Temp - p[i - 1].Temp);
                return p[i - 1].Duty + k * (p[i].Duty - p[i - 1].Duty);
            }
        return p[^1].Duty;
    }
}
