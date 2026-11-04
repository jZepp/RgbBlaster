namespace RgbBlaster.Drivers.Logitech;

/// <summary>
/// HID++ 2.0 long messages: [0x11, device index, feature index, function &lt;&lt; 4 | software id, 16 parameter bytes].
/// A reply echoes bytes 1-3. An error reply has feature index 0xFF, then the request's feature index, function byte and error code.
/// </summary>
public static class HidppProtocol
{
    public const ushort Vid = 0x046D;
    public const ushort PidG502Hero = 0xC08B;
    public const byte ReportLong = 0x11;
    public const int LongLength = 20;
    public const byte WiredDevice = 0xFF;
    public const byte SoftwareId = 0x0B;
    public const byte RootIndex = 0x00;
    public const byte ErrorIndex = 0xFF;
    public const int ParamOffset = 4;

    public const ushort FeatureColorLedEffects = 0x8070;
    // 0x8070 functions
    public const byte FnGetInfo = 0;
    public const byte FnGetZoneInfo = 1;
    public const byte FnGetEffectInfo = 2;
    public const byte FnSetZoneEffect = 3;
    public const byte FnSoftwareControl = 8;
    public const ushort EffectFixed = 0x0001;

    public static void Request(Span<byte> buf, byte featureIndex, byte function, ReadOnlySpan<byte> parameters)
    {
        buf[..LongLength].Clear();
        buf[0] = ReportLong;
        buf[1] = WiredDevice;
        buf[2] = featureIndex;
        buf[3] = (byte)((function << 4) | SoftwareId);
        parameters.CopyTo(buf[ParamOffset..]);
    }

    public static bool IsReplyTo(ReadOnlySpan<byte> r, byte featureIndex, byte function) =>
        r.Length >= LongLength && r[0] == ReportLong && r[1] == WiredDevice && r[2] == featureIndex && r[3] == (byte)((function << 4) | SoftwareId);

    public static bool IsErrorFor(ReadOnlySpan<byte> r, byte featureIndex, byte function) =>
        r.Length >= 7 && r[2] == ErrorIndex && r[3] == featureIndex && r[4] == (byte)((function << 4) | SoftwareId);

    /// <summary>0x8070 zone location codes.</summary>
    public static string ZoneName(ushort location) => location switch
    {
        1 => "Primary",
        2 => "Logo",
        _ => $"Zone {location}",
    };
}
