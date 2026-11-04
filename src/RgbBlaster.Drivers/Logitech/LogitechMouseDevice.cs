using HidSharp;
using RgbBlaster.Core;
using RgbBlaster.Drivers.Hid;
using P = RgbBlaster.Drivers.Logitech.HidppProtocol;

namespace RgbBlaster.Drivers.Logitech;

/// <summary>
/// Wired Logitech G mouse lighting over HID++ 2.0 feature 0x8070 (Color LED Effects). One color per zone.
/// Open takes software control of the lighting; Release hands it back to the onboard profile.
/// Protocol facts from OpenRGB's Logitech HID++ 2.0 controller.
/// </summary>
public sealed class LogitechMouseDevice : IRgbDevice
{
    private const int ReplyTimeoutMs = 500;

    private readonly IHidTransport _io;
    private readonly byte[] _out = new byte[P.LongLength];
    private readonly byte[] _in = new byte[64];
    private readonly object _ioLock = new();
    private readonly object _stage = new();
    private byte _feature;
    private List<(byte Zone, byte FixedEffect)> _zoneMap = [];
    private List<LedZoneInfo> _zones = [];
    private Rgb[] _staged = [];
    private Rgb[] _sent = [];
    private bool _sentValid;

    public LogitechMouseDevice(IHidTransport io, string name)
    {
        _io = io;
        Name = name;
    }

    public string Id => "mouse";
    public string Name { get; }
    public DeviceKind Kind => DeviceKind.Mouse;
    public IReadOnlyList<LedZoneInfo> Zones => _zones;

    public static IEnumerable<LogitechMouseDevice> Discover()
    {
        foreach (var d in DeviceList.Local.GetHidDevices(P.Vid, P.PidG502Hero))
            if (d.GetMaxOutputReportLength() == P.LongLength)
                yield return new LogitechMouseDevice(new HidSharpTransport(d), "Logitech G502 HERO");
    }

    public void Open()
    {
        lock (_ioLock)
        {
            var r = Call(P.RootIndex, 0, [(byte)(P.FeatureColorLedEffects >> 8), (byte)(P.FeatureColorLedEffects & 0xFF)]);
            _feature = r[P.ParamOffset];
            if (_feature == 0) throw new IOException($"{Name}: no HID++ feature 0x8070 (Color LED Effects).");

            int zones = Call(_feature, P.FnGetInfo, [])[P.ParamOffset];
            var map = new List<(byte, byte)>();
            var infos = new List<LedZoneInfo>();
            for (byte z = 0; z < zones; z++)
            {
                var zi = Call(_feature, P.FnGetZoneInfo, [z]);
                ushort location = (ushort)((zi[P.ParamOffset + 1] << 8) | zi[P.ParamOffset + 2]);
                int effects = zi[P.ParamOffset + 3];
                for (byte e = 0; e < effects; e++)
                {
                    var ei = Call(_feature, P.FnGetEffectInfo, [z, e]);
                    ushort id = (ushort)((ei[P.ParamOffset + 2] << 8) | ei[P.ParamOffset + 3]);
                    if (id != P.EffectFixed) continue;
                    map.Add((z, e));
                    infos.Add(new LedZoneInfo($"z{z}", P.ZoneName(location), 1));
                    break;
                }
            }
            _zoneMap = map;
            _zones = infos;
            _staged = new Rgb[map.Count];
            _sent = new Rgb[map.Count];
            _sentValid = false;
            Call(_feature, P.FnSoftwareControl, [1, 1]);
        }
    }

    public void SetZone(int zoneIndex, ReadOnlySpan<Rgb> colors)
    {
        if (colors.Length == 0) return;
        lock (_stage) _staged[zoneIndex] = colors[0];
    }

    public void Flush()
    {
        lock (_ioLock)
        {
            for (int i = 0; i < _zoneMap.Count; i++)
            {
                Rgb c;
                lock (_stage) c = _staged[i];
                if (_sentValid && _sent[i] == c) continue;
                var (zone, effect) = _zoneMap[i];
                // Byte 12 of the parameters (persistence) stays 0: the color is not stored on the mouse.
                Call(_feature, P.FnSetZoneEffect, [zone, effect, c.R, c.G, c.B]);
                _sent[i] = c;
            }
            _sentValid = true;
        }
    }

    public void Release()
    {
        lock (_ioLock) Call(_feature, P.FnSoftwareControl, [0, 0]);
    }

    public void Dispose() => _io.Dispose();

    /// <summary>Sends one request and returns its reply. Caller holds _ioLock.</summary>
    private ReadOnlySpan<byte> Call(byte feature, byte function, ReadOnlySpan<byte> parameters)
    {
        P.Request(_out, feature, function, parameters);
        _io.Write(_out);
        var deadline = Environment.TickCount64 + ReplyTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int n;
            try { n = _io.Read(_in, ReplyTimeoutMs); }
            catch (TimeoutException) { break; }
            var r = _in.AsSpan(0, n);
            if (P.IsReplyTo(r, feature, function)) return r;
            if (P.IsErrorFor(r, feature, function))
                throw new IOException($"{Name}: HID++ feature {feature} function {function} failed: error 0x{r[5]:X2}.");
            // Other traffic (notifications, other software IDs) is ignored.
        }
        throw new IOException($"{Name}: no HID++ reply from feature {feature} function {function} within {ReplyTimeoutMs} ms.");
    }
}
