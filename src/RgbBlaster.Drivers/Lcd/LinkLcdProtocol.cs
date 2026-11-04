namespace RgbBlaster.Drivers.Lcd;

/// <summary>
/// iCUE LINK AIO LCD (pump cap screen, USB 1B1C:0C4E). Its own USB device, separate from the LINK hub.
/// Frames are 480x480 JPEG images split into 1024-byte output reports:
/// [0x02, 0x05, 0x01, last ? 1 : 0, chunk index, 0, length lo, length hi, JPEG bytes from offset 8].
/// Control uses 32-byte feature reports [0x03, command, value, 0x01].
/// Protocol facts from OpenLinkHub (jurkovic-nikola/OpenLinkHub, lsh.go and lcd.go); this is an independent implementation.
/// </summary>
public static class LinkLcdProtocol
{
    public const ushort Vid = 0x1B1C;
    public const ushort PidAioLcd = 0x0C4E;
    public const int Width = 480, Height = 480;
    public const int ReportLength = 1024;
    public const int HeaderLength = 8;
    public const int MaxChunk = ReportLength - HeaderLength;
    public const byte FrameReportId = 0x02;
    public const byte FeatureReportId = 0x03;

    public const byte CmdBrightness = 0x0B;   // value 0-100; 0 turns the backlight off
    public const byte CmdRotation = 0x0C;     // value 0-3, in 90-degree steps
    public const byte CmdPower = 0x19;        // value 0 after backlight off
    public const byte CmdRelease1 = 0x1E;     // [1E 01] then [1D 00]: sent on exit, returns the screen to its own content
    public const byte CmdRelease2 = 0x1D;

    /// <summary>Fills <paramref name="buf"/> with chunk <paramref name="index"/> of a frame.</summary>
    public static void FrameChunk(Span<byte> buf, int index, ReadOnlySpan<byte> chunk, bool last)
    {
        if (chunk.Length > MaxChunk) throw new ArgumentOutOfRangeException(nameof(chunk));
        buf[..ReportLength].Clear();
        buf[0] = FrameReportId;
        buf[1] = 0x05;
        buf[2] = 0x01;
        buf[3] = (byte)(last ? 1 : 0);
        buf[4] = (byte)index;
        buf[6] = (byte)chunk.Length;
        buf[7] = (byte)(chunk.Length >> 8);
        chunk.CopyTo(buf[HeaderLength..]);
    }

    public static byte[] Feature(byte command, byte value) => [FeatureReportId, command, value, 0x01];
}
