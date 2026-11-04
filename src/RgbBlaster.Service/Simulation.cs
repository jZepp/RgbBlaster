using RgbBlaster.Core;

namespace RgbBlaster.Service;

/// <summary>
/// Stand-in hardware for developing the GUI without touching real devices. Started with --simulate.
/// One device per layout entry, fake fans for every fan named in a curve, and slowly varying temperatures.
/// </summary>
public static class Simulation
{
    public static List<IRgbDevice> Devices(IEnumerable<ZonePlacement> layout) =>
        layout.GroupBy(p => p.Device, StringComparer.OrdinalIgnoreCase)
            .Select(g => (IRgbDevice)new SimDevice(g.Key, g.Select(p => new LedZoneInfo(p.Zone, p.Zone, p.Shape switch
            {
                PlacementShape.Ring => 12,
                PlacementShape.Grid => 60,
                _ => 8,
            })).ToList()))
            .ToList();

    public static List<IFanChannel> Fans(IEnumerable<string> ids) =>
        ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => (IFanChannel)new SimFan(id)).ToList();

    private sealed class SimDevice(string id, IReadOnlyList<LedZoneInfo> zones) : IRgbDevice
    {
        public string Id => id;
        public string Name => $"Simulated {id}";
        public DeviceKind Kind => DeviceKind.Accessory;
        public IReadOnlyList<LedZoneInfo> Zones => zones;
        public void Open() { }
        public void SetZone(int zoneIndex, ReadOnlySpan<Rgb> colors) { }
        public void Flush() { }
        public void Release() { }
        public void Dispose() { }
    }

    private sealed class SimFan(string id) : IFanChannel
    {
        private byte _duty = 50;
        public string Id => id;
        public string Name => $"Simulated {id}";
        public bool IsPump => false;
        public int? ReadRpm() => _duty * 18;
        public void SetDutyPercent(byte percent) => _duty = percent;
    }

    public sealed class Temps : ITemperatureSource
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        public float? ReadCelsius(string sensor)
        {
            double t = _clock.Elapsed.TotalSeconds;
            return sensor.ToLowerInvariant() switch
            {
                "cpu" => (float)(55 + 15 * Math.Sin(t / 40)),
                "gpu" => (float)(45 + 10 * Math.Sin(t / 55 + 1)),
                _ => 35,
            };
        }
    }
}
