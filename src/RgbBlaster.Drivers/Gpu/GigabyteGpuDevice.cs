using RgbBlaster.Core;
using P = RgbBlaster.Drivers.Gpu.GigabyteGpuProtocol;

namespace RgbBlaster.Drivers.Gpu;

/// <summary>
/// Gigabyte RTX 50-series card lighting over NVAPI I2C. One color per zone.
/// Each I2C write is slow (about a millisecond), so <see cref="Flush"/> writes only zones that changed.
/// </summary>
public sealed class GigabyteGpuDevice : IRgbDevice, IPersistentLighting
{
    private readonly II2cBus _bus;
    private readonly int _hardwareZones;
    private readonly Rgb[] _staged;
    private readonly Rgb[] _sent;
    private readonly byte[] _buf = new byte[P.PacketSize];
    private readonly object _stage = new();
    private readonly object _ioLock = new();
    private bool _sentValid;

    public GigabyteGpuDevice(II2cBus bus, string model, GpuLayout layout)
    {
        _bus = bus;
        Name = $"Gigabyte {model}";
        Zones = P.ZoneNames(layout).Select((n, i) => new LedZoneInfo($"z{i}", n, 1)).ToList();
        _hardwareZones = layout == GpuLayout.Gaming ? P.HardwareZones : 1;
        _staged = new Rgb[Zones.Count];
        _sent = new Rgb[_hardwareZones];
    }

    public string Id => "gpu";
    public string Name { get; }
    public DeviceKind Kind => DeviceKind.Gpu;
    public IReadOnlyList<LedZoneInfo> Zones { get; }
    public bool CanSaveLighting => true;

    /// <summary>Finds known Gigabyte cards whose controller answers the probe.</summary>
    public static IEnumerable<GigabyteGpuDevice> Discover()
    {
        foreach (var g in NvApi.EnumerateGpus())
        {
            if (g.Vendor != P.NvidiaVendor || g.Device != P.Rtx5070TiDevice || g.SubVendor != P.GigabyteSubVendor) continue;
            if (!P.Rtx5070Ti.TryGetValue(g.SubDevice, out var card)) continue;
            var bus = new NvApi.Bus(g.Handle);
            if (Probe(bus)) yield return new GigabyteGpuDevice(bus, card.Name, card.Layout);
        }
    }

    /// <summary>Writes the probe packet and checks the 4-byte answer.</summary>
    public static bool Probe(II2cBus bus)
    {
        try
        {
            var buf = new byte[P.PacketSize];
            P.Simple(buf, P.RegProbe);
            bus.Write(P.Address, buf);
            var reply = new byte[4];
            bus.Read(P.Address, reply);
            return P.IsProbeReply(reply);
        }
        catch (IOException) { return false; }
    }

    public void Open() => _sentValid = false;

    public void SetZone(int zoneIndex, ReadOnlySpan<Rgb> colors)
    {
        if (colors.Length == 0) return;
        lock (_stage) _staged[zoneIndex] = colors[0];
    }

    public void Flush()
    {
        lock (_ioLock)
        {
            for (int hz = 0; hz < _hardwareZones; hz++)
            {
                var c = Color(hz);
                if (_sentValid && _sent[hz] == c) continue;
                P.ColorPacket(_buf, P.RegColor, P.ModeDirect, hz, c);
                _bus.Write(P.Address, _buf);
                _sent[hz] = c;
            }
            _sentValid = true;
        }
    }

    /// <summary>Nothing to hand back: the card keeps the last colors until its stored effect is set again.</summary>
    public void Release() { }

    /// <summary>Stores each zone's color as a static effect and saves it on the card.</summary>
    public void SaveLightingAndRelease()
    {
        lock (_ioLock)
        {
            for (int hz = 0; hz < _hardwareZones; hz++)
            {
                P.ColorPacket(_buf, P.RegMode, P.ModeStatic, hz, Color(hz));
                _bus.Write(P.Address, _buf);
            }
            P.Simple(_buf, P.RegSave);
            _bus.Write(P.Address, _buf);
        }
    }

    /// <summary>
    /// Diagnostic: lights one hardware zone (0-7) red with the given register, all other zones black.
    /// <see cref="P.RegMode"/> changes the card's current effect but is not saved without <see cref="P.RegSave"/>.
    /// </summary>
    public void ProbeZone(byte register, int hardwareZone, int zoneCount = 8)
    {
        byte mode = register == P.RegColor ? P.ModeDirect : P.ModeStatic;
        lock (_ioLock)
        {
            for (int hz = 0; hz < zoneCount; hz++)
            {
                P.ColorPacket(_buf, register, mode, hz, hz == hardwareZone ? new Rgb(255, 0, 0) : Rgb.Black);
                _bus.Write(P.Address, _buf);
            }
            _sentValid = false;
        }
    }

    private Rgb Color(int hardwareZone)
    {
        lock (_stage) return _staged[Math.Min(hardwareZone, _staged.Length - 1)];
    }

    public void Dispose() { }
}
