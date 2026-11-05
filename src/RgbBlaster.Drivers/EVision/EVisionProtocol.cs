namespace RgbBlaster.Drivers.EVision;

/// <summary>
/// EVision keyboard controller (Redragon K580 and similar, VID 0x320F). 64-byte output reports on interface 1, usage page 0xFF1C:
/// [0x04, checksum lo, checksum hi, command, length, offset/parameter..., data from byte 8].
/// The checksum is the 16-bit sum of bytes 3..63. Every write is answered with one 64-byte input report.
/// Protocol facts from OpenRGB's EVision keyboard controller.
/// </summary>
public static class EVisionProtocol
{
    public const ushort Vid = 0x320F;
    public const ushort PidK580 = 0x5000;
    public const int ReportLength = 64;
    public const byte ReportId = 0x04;
    public const byte CmdBegin = 0x01;
    public const byte CmdEnd = 0x02;
    public const byte CmdReadParameter = 0x05;
    public const byte CmdSetParameter = 0x06;
    public const byte CmdWriteColors = 0x11;
    public const int DataOffset = 8;
    public const int MaxColorBytes = 0x36;
    public const int LedCount = 126;
    public const int ColorBufferLength = LedCount * 3; // 7 packets of 54 bytes
    public const byte ModeColorWave = 0x02;   // firmware wave; with the random flag it is a rainbow
    public const byte ModeSpectrumCycle = 0x04;
    public const byte ModeStarlightSlow = 0x0F;
    public const byte ModeRainbowWaveVertical = 0x0C;
    // Direction values for the color wave, tested on a K580 on 2026-11-04. 2 and 3 also run right to left; no value runs vertically.
    public const byte DirectionLeftToRight = 0, DirectionRightToLeft = 1;
    public const byte ModeBreathing = 0x05;
    public const byte ModeStatic = 0x06;
    public const byte ModeCustom = 0x14;      // per-key colors; the keyboard stores them on every write (visible flash)
    public const byte SpeedFastest = 0x00, SpeedSlowest = 0x05;
    public const byte BrightnessMax = 0x04;
    public const byte SpeedNormal = 0x03;

    /// <summary>Built-in modes (OpenRGB names). Reactive modes light keys as they are pressed.</summary>
    public static readonly IReadOnlyList<(byte Mode, string Name)> Modes =
    [
        (0x01, "Color wave (short)"), (0x02, "Color wave (long)"), (0x03, "Color wheel"), (0x04, "Spectrum cycle"),
        (0x05, "Breathing"), (0x06, "Static"), (0x07, "Reactive"), (0x08, "Reactive ripple"), (0x09, "Reactive line"),
        (0x0A, "Starlight (fast)"), (0x0B, "Blooming"), (0x0C, "Rainbow wave (vertical)"), (0x0D, "Hurricane"),
        (0x0E, "Accumulate"), (0x0F, "Starlight (slow)"), (0x10, "Visor"), (0x11, "Surmount"), (0x12, "Rainbow wave (circle)"),
    ];

    // K580 side light bar (left edge). Stored config, written with CmdSetParameter. Found by diffing config reads and testing on hardware.
    public const byte SideBarModeAddress = 0x14;
    public const byte SideBarColorAddress = 0x16;
    public const byte SideBarModeWave = 0x00;      // moving rainbow (factory default)
    public const byte SideBarModeCycle = 0x01;     // whole bar cycles through colors
    public const byte SideBarModeBreathe = 0x02;   // fades in and out, changing color
    public const byte SideBarModeStatic = 0x03;    // one palette color from SideBarColorAddress
    public const byte SideBarModeOff = 0x04;

    /// <summary>Static side-bar palette by index. Index 2 (yellow) is probable; the others were confirmed from photos. Index 8 and above turns the bar off.</summary>
    public static readonly Core.Rgb[] SideBarPalette =
    [
        new(255, 0, 0), new(255, 128, 0), new(255, 255, 0), new(0, 255, 0),
        new(0, 200, 255), new(0, 0, 255), new(160, 0, 255), new(255, 255, 255),
    ];

    public const int SideBarWhiteIndex = 7;

    /// <summary>
    /// Side-bar setting for a requested color: null for off (dark), white for unsaturated colors, otherwise the palette entry nearest in hue.
    /// </summary>
    public static int? SideBarIndex(Core.Rgb c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max < 32) return null;
        if (max - min < max / 4) return SideBarWhiteIndex;
        float h = Hue(c);
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < SideBarWhiteIndex; i++)
        {
            float d = Math.Abs(h - Hue(SideBarPalette[i]));
            d = Math.Min(d, 360 - d);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static float Hue(Core.Rgb c)
    {
        float r = c.R, g = c.G, b = c.B, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d == 0) return 0;
        float h = max == r ? (g - b) / d : max == g ? 2 + (b - r) / d : 4 + (r - g) / d;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>Begin and end packets carry the command in bytes 1 and 3 and no checksum.</summary>
    public static void Control(Span<byte> buf, byte command)
    {
        buf[..ReportLength].Clear();
        buf[0] = ReportId;
        buf[1] = command;
        buf[3] = command;
    }

    /// <summary>Mode parameter block: mode, brightness, speed, direction, random flag, R, G, B.</summary>
    /// <param name="direction">Parameter 3. Value meanings are not documented; 0 is the firmware default.</param>
    public static void ModeEx(Span<byte> buf, byte mode, byte brightness, byte speed, Core.Rgb color, bool randomColor = false, byte direction = 0)
    {
        buf[..ReportLength].Clear();
        buf[0] = ReportId;
        buf[3] = CmdSetParameter;
        buf[4] = 8;
        buf[5] = 0; // parameter 0: mode block
        buf[8] = mode;
        buf[9] = brightness;
        buf[10] = speed;
        buf[11] = direction;
        buf[12] = (byte)(randomColor ? 1 : 0);
        buf[13] = color.R;
        buf[14] = color.G;
        buf[15] = color.B;
        Checksum(buf);
    }

    /// <summary>Requests <paramref name="length"/> config bytes from <paramref name="address"/>; the reply carries them from byte 8.</summary>
    public static void ReadParameter(Span<byte> buf, byte address, byte length)
    {
        buf[..ReportLength].Clear();
        buf[0] = ReportId;
        buf[3] = CmdReadParameter;
        buf[4] = length;
        buf[5] = address;
        Checksum(buf);
    }

    /// <summary>Writes config bytes at <paramref name="address"/>. The keyboard stores its config, so the change persists.</summary>
    public static void WriteParameter(Span<byte> buf, byte address, ReadOnlySpan<byte> data)
    {
        if (data.Length > ReportLength - DataOffset) throw new ArgumentOutOfRangeException(nameof(data));
        buf[..ReportLength].Clear();
        buf[0] = ReportId;
        buf[3] = CmdSetParameter;
        buf[4] = (byte)data.Length;
        buf[5] = address;
        data.CopyTo(buf[DataOffset..]);
        Checksum(buf);
    }

    public static void Colors(Span<byte> buf, int offset, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxColorBytes) throw new ArgumentOutOfRangeException(nameof(data));
        buf[..ReportLength].Clear();
        buf[0] = ReportId;
        buf[3] = CmdWriteColors;
        buf[4] = (byte)data.Length;
        buf[5] = (byte)offset;
        buf[6] = (byte)(offset >> 8);
        data.CopyTo(buf[DataOffset..]);
        Checksum(buf);
    }

    private static void Checksum(Span<byte> buf)
    {
        ushort sum = 0;
        for (int i = 3; i < ReportLength; i++) sum += buf[i];
        buf[1] = (byte)sum;
        buf[2] = (byte)(sum >> 8);
    }

    /// <summary>
    /// LED index per key position: 6 rows by 23 columns, -1 where there is no key. LED indices missing from the map are unused.
    /// </summary>
    public static readonly int[][] Matrix =
    [
        Row(23, (0, 0), (2, 1), (3, 2), (4, 3), (5, 4), (7, 5), (8, 6), (9, 7), (10, 8), (12, 9), (13, 10), (14, 11), (15, 12), (16, 14), (17, 15), (18, 16)),
        Row(23, (0, 21), (1, 22), (2, 23), (3, 24), (4, 25), (5, 26), (6, 27), (7, 28), (8, 29), (9, 30), (10, 31), (12, 32), (13, 33), (14, 34), (16, 35), (17, 36), (18, 37), (19, 38), (20, 39), (21, 40), (22, 41)),
        Row(23, (0, 42), (2, 43), (3, 44), (4, 45), (5, 46), (7, 47), (8, 48), (9, 49), (10, 50), (11, 51), (12, 52), (13, 53), (14, 54), (15, 55), (16, 56), (17, 57), (18, 58), (19, 59), (20, 60), (21, 61), (22, 62)),
        Row(23, (0, 63), (2, 64), (3, 65), (4, 66), (5, 67), (7, 68), (8, 69), (9, 70), (10, 71), (11, 72), (12, 73), (13, 74), (14, 76), (19, 80), (20, 81), (21, 82)),
        Row(23, (0, 84), (2, 86), (3, 87), (4, 88), (5, 89), (7, 90), (9, 91), (10, 92), (11, 93), (12, 94), (13, 95), (14, 97), (17, 99), (19, 101), (20, 102), (21, 103), (22, 104)),
        Row(23, (0, 105), (1, 106), (2, 107), (7, 108), (12, 109), (13, 110), (14, 111), (15, 113), (16, 119), (17, 120), (18, 121), (19, 123), (21, 124)),
    ];

    private static int[] Row(int columns, params (int Col, int Led)[] keys)
    {
        var r = Enumerable.Repeat(-1, columns).ToArray();
        foreach (var (c, l) in keys) r[c] = l;
        return r;
    }

    /// <summary>Position of each LED index in the key matrix, 0..1 on each axis. Unmapped LEDs get the center.</summary>
    public static (float X, float Y)[] Positions()
    {
        var pos = Enumerable.Repeat((0.5f, 0.5f), LedCount).ToArray();
        int rows = Matrix.Length, cols = Matrix[0].Length;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                if (Matrix[r][c] >= 0)
                    pos[Matrix[r][c]] = ((c + 0.5f) / cols, (r + 0.5f) / rows);
        return pos;
    }
}
