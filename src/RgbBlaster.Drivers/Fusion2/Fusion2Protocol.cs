using System.Buffers.Binary;
using System.Text;
using RgbBlaster.Core;

namespace RgbBlaster.Drivers.Fusion2;

/// <summary>
/// Gigabyte RGB Fusion 2 USB (ITE IT57xx) wire format. All packets are 64-byte feature reports with report ID 0xCC.
/// Source: protocol facts documented by the OpenRGB project. Items marked "inferred" are not stated there.
/// </summary>
public static class Fusion2Protocol
{
    public const ushort Vid = 0x048D;
    public const ushort PidIt5702 = 0x5702;
    public const ushort PidIt5711 = 0x5711;
    public static readonly ushort[] Pids = [PidIt5702, PidIt5711];

    public const byte ReportId = 0xCC;
    public const int ReportSize = 64;
    public const int MaxLedsPerPacket = 19;
    private const int RgbDataOffset = 5;

    public const byte CmdInfo = 0x60;
    public const byte CmdCalibration5711 = 0x61;
    public const byte CmdApply = 0x28;
    public const byte CmdBeat = 0x31;
    public const byte CmdDisableBuiltin = 0x32;
    public const byte CmdLedCount = 0x34;
    public const byte CmdLampArray = 0x48;

    /// <summary>Single-color onboard zones that do not alias the D_LED header indices 5..8.</summary>
    public static readonly int[] OnboardZones = [0, 1, 2, 3, 4, 9, 10];

    public static byte HeaderCommand(Fusion2Header h) => h switch
    {
        Fusion2Header.D_LED1 => 0x58,
        Fusion2Header.D_LED2 => 0x59,
        Fusion2Header.D_LED3 => 0x62,
        Fusion2Header.D_LED4 => 0x63,
        Fusion2Header.ONBOARD1 => 0x64,
        Fusion2Header.ONBOARD2 => 0x65,
        _ => throw new ArgumentOutOfRangeException(nameof(h)),
    };

    /// <summary>Bit in the 0x32 mask that stops the firmware's own effect on a header.</summary>
    public static byte BuiltinDisableBit(Fusion2Header h) => h switch
    {
        Fusion2Header.D_LED1 => 0x01,
        Fusion2Header.D_LED2 => 0x02,
        Fusion2Header.D_LED3 => 0x08,
        Fusion2Header.D_LED4 => 0x10,
        Fusion2Header.ONBOARD1 => 0x20,
        Fusion2Header.ONBOARD2 => 0x40,
        _ => throw new ArgumentOutOfRangeException(nameof(h)),
    };

    /// <summary>Slot in the 0x34 count nibbles and in the calibration table. Inferred from the calibration ordering.</summary>
    public static int HeaderSlot(Fusion2Header h) => (int)h;

    public static void Cc(Span<byte> buf, byte a, byte b = 0, byte c = 0)
    {
        buf[..ReportSize].Clear();
        buf[0] = ReportId;
        buf[1] = a;
        buf[2] = b;
        buf[3] = c;
    }

    public static byte ApplyMask(ushort pid) => pid == PidIt5711 ? (byte)0x07 : (byte)0x00;

    /// <summary>Encodes an LED count as the firmware's length bucket.</summary>
    public static byte CountBucket(int leds) => leds switch
    {
        <= 32 => 0,
        <= 64 => 1,
        <= 256 => 2,
        <= 512 => 3,
        _ => 4,
    };

    public static void LedCounts(Span<byte> buf, ReadOnlySpan<byte> buckets)
    {
        Cc(buf, CmdLedCount);
        buf[2] = (byte)((buckets[1] << 4) | buckets[0]);
        buf[3] = (byte)((buckets[3] << 4) | buckets[2]);
        buf[4] = (byte)((buckets[5] << 4) | buckets[4]);
    }

    /// <summary>Builds one direct-color packet for up to 19 LEDs starting at <paramref name="firstLed"/>.</summary>
    public static void RgbPacket(Span<byte> buf, byte header, int firstLed, ReadOnlySpan<Rgb> leds, ColorOrder order)
    {
        if (leds.Length > MaxLedsPerPacket) throw new ArgumentOutOfRangeException(nameof(leds));
        buf[..ReportSize].Clear();
        buf[0] = ReportId;
        buf[1] = header;
        BinaryPrimitives.WriteUInt16LittleEndian(buf[2..], (ushort)(firstLed * 3));
        buf[4] = (byte)(leds.Length * 3);
        var data = buf[RgbDataOffset..];
        for (int i = 0; i < leds.Length; i++)
        {
            int o = i * 3;
            data[o + order.R] = leds[i].R;
            data[o + order.G] = leds[i].G;
            data[o + order.B] = leds[i].B;
        }
    }

    /// <summary>Static color on one onboard zone (0..10). Color is stored B, G, R.</summary>
    public static void StaticZone(Span<byte> buf, int zone, Rgb color)
    {
        buf[..ReportSize].Clear();
        buf[0] = ReportId;
        buf[1] = zone < 8 ? (byte)(0x20 + zone) : (byte)(0x90 + zone - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[2..], 1u << zone);
        buf[11] = 1;    // static
        buf[12] = 0xFF; // max brightness
        buf[14] = color.B;
        buf[15] = color.G;
        buf[16] = color.R;
    }

    public static Fusion2Info ParseInfo(ReadOnlySpan<byte> r)
    {
        var name = Encoding.ASCII.GetString(r.Slice(12, 28)).Split('\0')[0].Trim();
        var cal = new uint[6];
        cal[0] = BinaryPrimitives.ReadUInt32LittleEndian(r[44..]);
        cal[1] = BinaryPrimitives.ReadUInt32LittleEndian(r[48..]);
        return new Fusion2Info(
            Product: name,
            DeviceNum: r[2],
            StripDetect: r[3],
            Firmware: $"{r[4]}.{r[5]}.{r[6]}.{r[7]}",
            CountNibbles: [(byte)(r[8] & 0x0F), (byte)(r[8] >> 4), (byte)(r[9] & 0x0F), (byte)(r[9] >> 4), (byte)(r[10] & 0x0F), (byte)(r[10] >> 4)],
            SupportFlags: r[11],
            ChipId: BinaryPrimitives.ReadUInt32LittleEndian(r[56..]),
            Calibration: cal);
    }

    /// <summary>Fills calibration slots 2..5 from the IT5711 0x61 response.</summary>
    public static void ParseCalibration5711(ReadOnlySpan<byte> r, uint[] cal)
    {
        for (int i = 0; i < 4; i++)
            cal[2 + i] = BinaryPrimitives.ReadUInt32LittleEndian(r[(4 + 4 * i)..]);
    }
}

public sealed record Fusion2Info(
    string Product, byte DeviceNum, byte StripDetect, string Firmware,
    byte[] CountNibbles, byte SupportFlags, uint ChipId, uint[] Calibration);

/// <summary>Byte position of each channel inside a 3-byte LED triplet.</summary>
public readonly record struct ColorOrder(int R, int G, int B)
{
    public static readonly ColorOrder Rgb = new(0, 1, 2);
    public static readonly ColorOrder Grb = new(1, 0, 2);

    public bool IsValid => R is >= 0 and <= 2 && G is >= 0 and <= 2 && B is >= 0 and <= 2 && R != G && G != B && R != B;

    /// <summary>Calibration word: bits 16..23 = R position, 8..15 = G, 0..7 = B.</summary>
    public static ColorOrder FromCalibration(uint v) => new((int)(v >> 16) & 0xFF, (int)(v >> 8) & 0xFF, (int)v & 0xFF);

    public static ColorOrder FromSetting(ColorOrderSetting s) => s switch
    {
        ColorOrderSetting.RGB => FromName("RGB"),
        ColorOrderSetting.GRB => FromName("GRB"),
        ColorOrderSetting.BRG => FromName("BRG"),
        ColorOrderSetting.RBG => FromName("RBG"),
        ColorOrderSetting.GBR => FromName("GBR"),
        ColorOrderSetting.BGR => FromName("BGR"),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static ColorOrder FromName(string n) => new(n.IndexOf('R'), n.IndexOf('G'), n.IndexOf('B'));
}
