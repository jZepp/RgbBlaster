using System.Buffers.Binary;
using System.Text;

namespace RgbBlaster.Drivers.Link;

/// <summary>
/// Corsair iCUE LINK System Hub wire format.
/// Sources: protocol facts documented by OpenRGB (CorsairICueLinkController) and FanControl.CorsairLink.
/// Raw buffers carry HID report ID 0x00 at index 0. Response offsets below are on the buffer without that byte.
/// </summary>
public static class LinkProtocol
{
    public const ushort Vid = 0x1B1C;
    public const ushort PidSystemHub = 0x0C3F;

    public const int WriteLength = 513;
    /// <summary>Largest slice of a framed write that fits one report after the 3-byte prefix and 2-byte command.</summary>
    public const int MaxChunk = 508;

    public static ReadOnlySpan<byte> CmdFirmware => [0x02, 0x13];
    public static ReadOnlySpan<byte> CmdSoftwareMode => [0x01, 0x03, 0x00, 0x02];
    public static ReadOnlySpan<byte> CmdHardwareMode => [0x01, 0x03, 0x00, 0x01];
    public static ReadOnlySpan<byte> CmdOpen => [0x0D, 0x01];
    public static ReadOnlySpan<byte> CmdOpenColor => [0x0D, 0x00];
    public static ReadOnlySpan<byte> CmdClose => [0x05, 0x01, 0x01];
    public static ReadOnlySpan<byte> CmdRead => [0x08, 0x01];
    public static ReadOnlySpan<byte> CmdWrite => [0x06, 0x01];
    public static ReadOnlySpan<byte> CmdWriteColor => [0x06, 0x00];
    public static ReadOnlySpan<byte> CmdWriteColorNext => [0x07, 0x00];

    public const byte EpSpeeds = 0x17;
    public const byte EpSetSpeed = 0x18;
    public const byte EpTemperatures = 0x21;
    public const byte EpColor = 0x22;
    public const byte EpDevices = 0x36;

    public static ReadOnlySpan<byte> TypeSpeeds => [0x25, 0x00];
    public static ReadOnlySpan<byte> TypeSetSpeed => [0x07, 0x00];
    public static ReadOnlySpan<byte> TypeTemperatures => [0x10, 0x00];
    public static ReadOnlySpan<byte> TypeColor => [0x12, 0x00];
    public static ReadOnlySpan<byte> TypeDevices => [0x21, 0x00];

    public const byte StatusOk = 0x00;
    public const byte StatusWrongMode = 0x03;

    /// <summary>[0]=report ID 0, [1]=0, [2]=1, command, data; zero-padded to 513 bytes.</summary>
    public static int Command(Span<byte> buf, ReadOnlySpan<byte> cmd, ReadOnlySpan<byte> data = default)
    {
        int len = 3 + cmd.Length + data.Length;
        if (len > WriteLength) throw new ArgumentException($"Command is {len} bytes; limit is {WriteLength}.");
        buf[..WriteLength].Clear();
        buf[2] = 0x01;
        cmd.CopyTo(buf[3..]);
        data.CopyTo(buf[(3 + cmd.Length)..]);
        return WriteLength;
    }

    /// <summary>Endpoint write frame: length (data + 2, LE16), 2 zero bytes, 2-byte data type, data.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> dataType, ReadOnlySpan<byte> data)
    {
        var f = new byte[4 + dataType.Length + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(f, (ushort)(data.Length + 2));
        dataType.CopyTo(f.AsSpan(4));
        data.CopyTo(f.AsSpan(4 + dataType.Length));
        return f;
    }

    /// <summary>Fixed-percent speed payload: count, then per channel {channel, 0, percent, 0}.</summary>
    public static byte[] SpeedPayload(IReadOnlyList<(byte Channel, byte Percent)> channels)
    {
        var p = new byte[1 + 4 * channels.Count];
        p[0] = (byte)channels.Count;
        for (int i = 0; i < channels.Count; i++)
        {
            p[1 + 4 * i] = channels[i].Channel;
            p[3 + 4 * i] = Math.Min(channels[i].Percent, (byte)100);
        }
        return p;
    }

    public static (byte Status, ushort Type) Header(ReadOnlySpan<byte> r) => (r[3], BinaryPrimitives.ReadUInt16LittleEndian(r[4..]));

    public static ushort TypeValue(ReadOnlySpan<byte> type) => BinaryPrimitives.ReadUInt16LittleEndian(type);

    public static Version ParseFirmware(ReadOnlySpan<byte> r) =>
        new(r[4], r[5], BinaryPrimitives.ReadUInt16LittleEndian(r[6..]));

    /// <summary>Firmware 2.5 and later split the device list over two reads.</summary>
    public static bool DeviceListHasContinuation(Version fw) => fw.Major >= 3 || (fw.Major == 2 && fw.Minor >= 5);

    /// <param name="primary">Device-list response without the report ID byte.</param>
    /// <param name="continuation">Second response for firmware 2.5+, or empty.</param>
    public static List<LinkSubDevice> ParseDevices(ReadOnlySpan<byte> primary, ReadOnlySpan<byte> continuation)
    {
        var payload = new List<byte>(primary.Length + continuation.Length);
        foreach (var b in primary[6..]) payload.Add(b);
        if (continuation.Length > 4) foreach (var b in continuation[4..]) payload.Add(b);
        var data = payload.ToArray().AsSpan();

        var result = new List<LinkSubDevice>();
        int lastChannel = data[0];
        int pos = 1;
        for (int ch = 1; ch <= lastChannel && pos + 8 <= data.Length; ch++)
        {
            var rec = data[pos..];
            int idLen = rec[7];
            if (idLen == 0) { pos += 8; continue; }
            int avail = Math.Min(idLen, rec.Length - 8);
            var idBytes = rec.Slice(8, avail);
            int nul = idBytes.IndexOf((byte)0);
            var id = Encoding.ASCII.GetString(nul >= 0 ? idBytes[..nul] : idBytes);
            result.Add(new LinkSubDevice((byte)ch, rec[2], rec[3], id));
            if (avail < idLen) break;
            pos += 8 + id.Length;
        }
        return result;
    }

    /// <summary>Speed and temperature responses: count at 6, then {status, int16 LE} per sensor from 7. Status 0 = available.</summary>
    public static short?[] ParseSensors(ReadOnlySpan<byte> r)
    {
        int n = r[6];
        var v = new short?[n];
        for (int i = 0; i < n && 7 + 3 * i + 2 < r.Length; i++)
        {
            var s = r[(7 + 3 * i)..];
            v[i] = s[0] == 0 ? BinaryPrimitives.ReadInt16LittleEndian(s[1..]) : null;
        }
        return v;
    }
}

public enum LinkKind { Fan, Cooler, Pump, Block, Case, Other }

public sealed record LinkModel(string Name, int Leds, LinkKind Kind, bool Internal = false)
{
    public bool HasSpeedControl => Kind is LinkKind.Fan or LinkKind.Cooler or LinkKind.Pump;
}

public sealed record LinkSubDevice(byte Channel, byte Type, byte Model, string Serial)
{
    public LinkModel? Info => LinkModels.Find(Type, Model);
}

/// <summary>Type/model table. LED counts from OpenRGB's device table.</summary>
public static class LinkModels
{
    private static readonly Dictionary<(byte, byte), LinkModel> Table = new()
    {
        [(0x01, 0x00)] = new("QX RGB fan", 34, LinkKind.Fan),
        [(0x02, 0x00)] = new("LX RGB fan", 18, LinkKind.Fan),
        [(0x03, 0x00)] = new("RX RGB MAX fan", 8, LinkKind.Fan),
        [(0x04, 0x00)] = new("RX MAX fan", 0, LinkKind.Fan),
        [(0x0F, 0x00)] = new("RX RGB fan", 8, LinkKind.Fan),
        [(0x13, 0x00)] = new("RX fan", 0, LinkKind.Fan),
        [(0x10, 0x00)] = new("VRM cooler module", 0, LinkKind.Fan),
        [(0x05, 0x00)] = new("LINK adapter", 0, LinkKind.Other),
        [(0x05, 0x01)] = new("9000D RGB AIRFLOW", 22, LinkKind.Case),
        [(0x05, 0x02)] = new("5000T RGB", 160, LinkKind.Case),
        [(0x06, 0x00)] = new("Cooler pump LCD", 24, LinkKind.Other),
        [(0x07, 0x00)] = new("H100i RGB", 20, LinkKind.Cooler),
        [(0x07, 0x01)] = new("H115i RGB", 20, LinkKind.Cooler),
        [(0x07, 0x02)] = new("H150i RGB", 20, LinkKind.Cooler),
        [(0x07, 0x03)] = new("H170i RGB", 20, LinkKind.Cooler),
        [(0x07, 0x04)] = new("H100i RGB", 20, LinkKind.Cooler),
        [(0x07, 0x05)] = new("H150i RGB", 20, LinkKind.Cooler),
        [(0x11, 0x00)] = new("TITAN 240", 20, LinkKind.Cooler),
        [(0x11, 0x01)] = new("TITAN 280", 20, LinkKind.Cooler),
        [(0x11, 0x02)] = new("TITAN 360", 20, LinkKind.Cooler),
        [(0x11, 0x03)] = new("TITAN 420", 20, LinkKind.Cooler),
        [(0x11, 0x04)] = new("TITAN 240", 20, LinkKind.Cooler),
        [(0x11, 0x05)] = new("TITAN 360", 20, LinkKind.Cooler),
        [(0x09, 0x00)] = new("XC7 ELITE", 24, LinkKind.Block),
        [(0x0A, 0x00)] = new("XG3 HYBRID", 0, LinkKind.Block),
        [(0x0D, 0x00)] = new("XG7 RGB", 16, LinkKind.Block),
        [(0x0C, 0x00)] = new("XD5 ELITE", 22, LinkKind.Pump),
        [(0x0E, 0x00)] = new("XD5 ELITE LCD", 22, LinkKind.Pump, Internal: true),
        [(0x19, 0x00)] = new("XD6 ELITE", 22, LinkKind.Pump),
        [(0x14, 0x00)] = new("ORIGIN OA", 0, LinkKind.Other),
    };

    public static LinkModel? Find(byte type, byte model) => Table.GetValueOrDefault((type, model));
}
