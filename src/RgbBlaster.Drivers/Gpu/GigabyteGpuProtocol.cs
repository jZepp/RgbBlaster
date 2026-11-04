using RgbBlaster.Core;

namespace RgbBlaster.Drivers.Gpu;

public enum GpuLayout { SingleZone, Gaming }

/// <summary>
/// Gigabyte RGB Fusion 2 controller on RTX 50-series cards, at I2C address 0x75.
/// Every packet is a 64-byte raw write: [register, 0x01, mode, speed, brightness, R, G, B, 0, zone, color count, ...].
/// Protocol facts from OpenRGB's GigabyteRGBFusion2BlackwellGPU controller.
/// </summary>
public static class GigabyteGpuProtocol
{
    public const byte Address = 0x75;
    public const int PacketSize = 64;
    public const byte RegMode = 0x12;   // stored effect
    public const byte RegColor = 0x16;  // live color
    public const byte RegSave = 0x13;
    public const byte RegProbe = 0x10;
    public const byte ModeDirect = 0x00;
    public const byte ModeStatic = 0x01;
    public const byte BrightnessMax = 0x0A;
    public const int HardwareZones = 6;
    public const ushort NvidiaVendor = 0x10DE;
    public const ushort GigabyteSubVendor = 0x1458;
    public const ushort Rtx5070TiDevice = 0x2C05;

    public static void ColorPacket(Span<byte> buf, byte register, byte mode, int zone, Rgb color)
    {
        buf[..PacketSize].Clear();
        buf[0] = register;
        buf[1] = 0x01;
        buf[2] = mode;
        buf[4] = BrightnessMax; // byte 3 (speed) stays 0: unused by direct and static
        buf[5] = color.R;
        buf[6] = color.G;
        buf[7] = color.B;
        buf[9] = (byte)zone;    // byte 10 (extra color count) stays 0
    }

    public static void Simple(Span<byte> buf, byte register)
    {
        buf[..PacketSize].Clear();
        buf[0] = register;
        buf[1] = 0x01;
    }

    /// <summary>Probe answer: 01 01 01 xx on NVIDIA cards (01 02 01 xx seen on AMD).</summary>
    public static bool IsProbeReply(ReadOnlySpan<byte> r) => r.Length >= 3 && r[0] == 0x01 && r[1] is 0x01 or 0x02 && r[2] == 0x01;

    /// <summary>Known Gigabyte RTX 5070 Ti cards by PCI subsystem device ID.</summary>
    public static readonly IReadOnlyDictionary<ushort, (string Name, GpuLayout Layout)> Rtx5070Ti = new Dictionary<ushort, (string, GpuLayout)>
    {
        [0x417F] = ("RTX 5070 Ti Aero OC", GpuLayout.SingleZone),
        [0x4180] = ("RTX 5070 Ti Eagle OC", GpuLayout.SingleZone),
        // OpenRGB lists this card with the 4-zone Gaming layout, but on the tested card every zone lit only the side LED.
        [0x4181] = ("RTX 5070 Ti Gaming OC", GpuLayout.SingleZone),
        [0x4182] = ("RTX 5070 Ti Eagle OC ICE", GpuLayout.SingleZone),
    };

    /// <summary>Zones in hardware zone order. Hardware zones past the list take the last zone's color.</summary>
    public static IReadOnlyList<string> ZoneNames(GpuLayout layout) => layout switch
    {
        GpuLayout.Gaming => ["Right fan", "Left fan", "Center fan", "Side"],
        _ => ["Side"],
    };
}
