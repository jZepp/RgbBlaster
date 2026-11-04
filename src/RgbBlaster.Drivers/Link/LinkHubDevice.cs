using HidSharp;
using RgbBlaster.Core;
using RgbBlaster.Drivers.Hid;
using L = RgbBlaster.Drivers.Link.LinkProtocol;

namespace RgbBlaster.Drivers.Link;

/// <summary>
/// iCUE LINK System Hub: lighting for every LED-capable subdevice, plus fan/pump duty, RPM and temperatures.
/// One I/O lock serialises the light worker and the fan poller; every command is a write followed by its response.
/// The hub stays in software mode only while this object is open. <see cref="Release"/> returns it to hardware mode,
/// where the hub runs its stored lighting and fan behaviour.
/// </summary>
public sealed class LinkHubDevice : IRgbDevice, IFanDevice
{
    public const byte DefaultFanPercent = 50;
    public const byte DefaultPumpPercent = 100;

    private sealed class Channel(LinkSubDevice dev, LinkHubDevice hub) : IFanChannel
    {
        public readonly LinkSubDevice Dev = dev;
        public byte Requested = dev.Info?.Kind is LinkKind.Cooler or LinkKind.Pump ? DefaultPumpPercent : DefaultFanPercent;
        public int? Rpm;
        public string Id => $"ch{Dev.Channel}";
        public string Name => $"{Dev.Info?.Name ?? "Unknown"} (ch {Dev.Channel})";
        public bool IsPump => Dev.Info?.Kind is LinkKind.Cooler or LinkKind.Pump;
        public int? ReadRpm() => Rpm;
        public void SetDutyPercent(byte percent) => hub.SetDuty(this, percent);
    }

    private readonly IHidTransport _io;
    private readonly byte[] _out = new byte[L.WriteLength];
    private readonly byte[] _in = new byte[L.WriteLength];
    private readonly object _ioLock = new();
    private readonly object _stage = new();
    private readonly List<Channel> _fans = [];
    private readonly List<(LinkSubDevice Dev, int Offset, int Count)> _lightMap = [];
    private Rgb[] _staged = [];
    private Rgb[] _sending = [];
    private Rgb[] _sent = [];
    private bool _sentValid;
    private readonly System.Diagnostics.Stopwatch _sinceSend = new();
    private bool _dutyDirty = true;
    private bool _needSoftwareMode;
    private List<LedZoneInfo> _zones = [];

    public LinkHubDevice(IHidTransport io, byte pumpMinPercent = 50)
    {
        _io = io;
        PumpMinPercent = Math.Min(pumpMinPercent, (byte)100);
    }

    public string Id => "link";
    public string Name => "Corsair iCUE LINK System Hub";
    public DeviceKind Kind => DeviceKind.FanHub;
    public IReadOnlyList<LedZoneInfo> Zones => _zones;
    public IReadOnlyList<IFanChannel> Fans => _fans;
    public byte PumpMinPercent { get; }
    public Version? Firmware { get; private set; }
    public IReadOnlyList<LinkSubDevice> SubDevices { get; private set; } = [];
    /// <summary>Temperature per sensor index, °C. Index matches the hub's channel number (unverified).</summary>
    public IReadOnlyList<float?> Temperatures { get; private set; } = [];
    /// <summary>Device-list responses (without report ID) from the last Open, for diagnostics.</summary>
    public (byte[] Primary, byte[] Continuation) RawDeviceList { get; private set; } = ([], []);

    public static IEnumerable<LinkHubDevice> Discover()
    {
        foreach (var d in DeviceList.Local.GetHidDevices(L.Vid, L.PidSystemHub))
            if (d.GetMaxOutputReportLength() >= L.WriteLength)
                yield return new LinkHubDevice(new HidSharpTransport(d));
    }

    public void Open()
    {
        lock (_ioLock)
        {
            Firmware = L.ParseFirmware(Send(L.CmdFirmware));
            Send(L.CmdSoftwareMode);
            SubDevices = ReadDevices();
        }

        _fans.Clear();
        _lightMap.Clear();
        _zones = [];
        int offset = 0;
        foreach (var d in SubDevices.OrderBy(d => d.Channel))
        {
            var info = d.Info;
            if (info is null) continue;
            if (info.HasSpeedControl) _fans.Add(new Channel(d, this));
            if (info.Leds > 0 && !info.Internal)
            {
                _lightMap.Add((d, offset, info.Leds));
                _zones.Add(new LedZoneInfo($"ch{d.Channel}", $"{info.Name} (ch {d.Channel})", info.Leds));
                offset += info.Leds;
            }
        }
        _staged = new Rgb[offset];
        _sending = new Rgb[offset];
        _sent = new Rgb[offset];
        _sentValid = false;
        _dutyDirty = true;
        Poll();
    }

    public void SetZone(int zoneIndex, ReadOnlySpan<Rgb> colors)
    {
        var (_, off, count) = _lightMap[zoneIndex];
        lock (_stage) colors[..Math.Min(colors.Length, count)].CopyTo(_staged.AsSpan(off, count));
    }

    /// <summary>
    /// The hub drops back to its stored lighting when it gets no color frame for a while.
    /// <see cref="Flush"/> resends an unchanged frame once this interval has passed.
    /// </summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sends all LEDs in channel order as one color frame when anything changed or <see cref="KeepAlive"/> expired.
    /// Assumption to verify on hardware: the hub expects every LED-capable device, concatenated by channel.
    /// Safe to call from several threads.
    /// </summary>
    public void Flush()
    {
        lock (_ioLock)
        {
            lock (_stage) _staged.CopyTo(_sending, 0);
            if (_sentValid && _sending.AsSpan().SequenceEqual(_sent) && _sinceSend.Elapsed < KeepAlive) return;

            var rgb = new byte[_sending.Length * 3];
            for (int i = 0; i < _sending.Length; i++)
            {
                rgb[3 * i] = _sending[i].R;
                rgb[3 * i + 1] = _sending[i].G;
                rgb[3 * i + 2] = _sending[i].B;
            }
            var frame = L.Frame(L.TypeColor, rgb);

            EnsureMode();
            Send(L.CmdClose, [L.EpColor]);
            Send(L.CmdOpenColor, [L.EpColor]);
            for (int pos = 0; pos < frame.Length; pos += L.MaxChunk)
            {
                var chunk = frame.AsSpan(pos, Math.Min(L.MaxChunk, frame.Length - pos));
                Send(pos == 0 ? L.CmdWriteColor : L.CmdWriteColorNext, chunk);
            }
            Send(L.CmdClose, [L.EpColor]);
            _sending.CopyTo(_sent, 0);
            _sentValid = true;
            _sinceSend.Restart();
        }
    }

    private void SetDuty(Channel ch, byte percent)
    {
        byte min = ch.IsPump ? PumpMinPercent : (byte)0;
        byte v = Math.Clamp(percent, min, (byte)100);
        lock (_ioLock)
        {
            if (ch.Requested == v) return;
            ch.Requested = v;
            _dutyDirty = true;
        }
    }

    /// <summary>Writes pending duty changes and refreshes RPM and temperatures. Call about once per second.</summary>
    public void Poll()
    {
        lock (_ioLock)
        {
            EnsureMode();
            if (_dutyDirty && _fans.Count > 0)
            {
                var payload = L.SpeedPayload(_fans.Select(f => (f.Dev.Channel, f.Requested)).ToList());
                WriteEndpoint(L.EpSetSpeed, L.Frame(L.TypeSetSpeed, payload));
                _dutyDirty = false;
            }

            var speeds = L.ParseSensors(ReadEndpoint(L.EpSpeeds, L.TypeSpeeds));
            foreach (var f in _fans)
                f.Rpm = f.Dev.Channel < speeds.Length ? speeds[f.Dev.Channel] : null;

            var temps = L.ParseSensors(ReadEndpoint(L.EpTemperatures, L.TypeTemperatures));
            Temperatures = temps.Select(t => t is null ? (float?)null : t.Value / 10f).ToArray();
        }
    }

    public void Release()
    {
        lock (_ioLock) Send(L.CmdHardwareMode);
    }

    public void Dispose() => _io.Dispose();

    private List<LinkSubDevice> ReadDevices()
    {
        var primary = ReadEndpoint(L.EpDevices, L.TypeDevices, keepOpen: true);
        byte[] cont = [];
        try
        {
            if (Firmware is not null && L.DeviceListHasContinuation(Firmware))
                cont = Send(L.CmdRead).ToArray();
        }
        finally { Send(L.CmdClose, [L.EpDevices]); }
        RawDeviceList = (primary, cont);
        return L.ParseDevices(primary, cont);
    }

    private byte[] ReadEndpoint(byte ep, ReadOnlySpan<byte> type, bool keepOpen = false)
    {
        Send(L.CmdClose, [ep]);
        Send(L.CmdOpen, [ep]);
        ushort want = L.TypeValue(type);
        byte[]? result = null;
        for (int attempt = 0; attempt < 5 && result is null; attempt++)
        {
            var r = Send(L.CmdRead);
            if (L.Header(r).Type == want) result = r.ToArray();
        }
        if (!keepOpen) Send(L.CmdClose, [ep]);
        return result ?? throw new IOException($"LINK hub endpoint 0x{ep:X2}: no 0x{want:X4} response after 5 reads.");
    }

    private void WriteEndpoint(byte ep, byte[] frame)
    {
        Send(L.CmdClose, [ep]);
        Send(L.CmdOpen, [ep]);
        Send(L.CmdWrite, frame);
        Send(L.CmdClose, [ep]);
    }

    private void EnsureMode()
    {
        if (!_needSoftwareMode) return;
        _needSoftwareMode = false;
        Send(L.CmdSoftwareMode);
        _sentValid = false; // the hub shows its stored lighting again until it gets a full frame
    }

    /// <summary>Writes one command and returns its response without the report ID byte. Caller holds _ioLock.</summary>
    private ReadOnlySpan<byte> Send(ReadOnlySpan<byte> cmd, ReadOnlySpan<byte> data = default)
    {
        L.Command(_out, cmd, data);
        _io.Write(_out);
        int n = _io.Read(_in, 1000);
        if (n <= 1) throw new IOException("LINK hub did not answer within 1000 ms.");
        var r = _in.AsSpan(1, n - 1);
        byte status = r[3];
        if (status == L.StatusWrongMode) _needSoftwareMode = true;
        else if (status != L.StatusOk && !cmd.SequenceEqual(L.CmdFirmware))
            throw new IOException($"LINK hub command {Convert.ToHexString(cmd)} failed: status 0x{status:X2}.");
        return r;
    }
}
