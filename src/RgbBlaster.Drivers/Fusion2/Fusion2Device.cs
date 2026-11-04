using System.Diagnostics;
using HidSharp;
using RgbBlaster.Core;
using RgbBlaster.Drivers.Hid;
using P = RgbBlaster.Drivers.Fusion2.Fusion2Protocol;

namespace RgbBlaster.Drivers.Fusion2;

/// <summary>
/// Gigabyte Fusion 2 USB motherboard controller.
/// Zones: one per configured D_LED/ONBOARD addressable header, plus "onboard" (one slot per single-color board zone).
/// Only 19-LED chunks whose bytes changed are sent. A full refresh runs once per second so a dropped report self-heals.
/// </summary>
public sealed class Fusion2Device : IRgbDevice
{
    private sealed class Strip(Fusion2HeaderConfig cfg)
    {
        public readonly Fusion2HeaderConfig Config = cfg;
        public readonly byte Command = P.HeaderCommand(cfg.Header);
        public readonly Rgb[] Staged = new Rgb[cfg.LedCount];
        public readonly Rgb[] Sending = new Rgb[cfg.LedCount];
        public readonly Rgb[] Sent = new Rgb[cfg.LedCount];
        public ColorOrder Order = ColorOrder.Rgb;
    }

    private const long FullRefreshMs = 1000;

    private readonly IHidTransport _io;
    private readonly ushort _pid;
    private readonly Strip[] _strips;
    private readonly int[] _onboardZones;
    private readonly bool _forceLampArrayOff;
    private readonly Rgb[] _onboardStaged;
    private readonly Rgb[] _onboardSending;
    private readonly Rgb[] _onboardSent;
    private readonly byte[] _buf = new byte[P.ReportSize];
    private readonly object _stage = new();
    private readonly object _ioLock = new();
    private readonly Stopwatch _sinceFull = new();
    private bool _onboardValid;

    public Fusion2Device(IHidTransport io, ushort pid, Fusion2Config config, string id = "fusion2")
    {
        _io = io;
        _pid = pid;
        Id = id;
        _forceLampArrayOff = config.ForceLampArrayOff;
        _onboardZones = config.OnboardZones is { Count: > 0 } oz ? oz.ToArray() : P.OnboardZones;
        if (_onboardZones.Any(z => z is < 0 or > 10)) throw new ArgumentException("Fusion 2 onboard zones must be 0-10.", nameof(config));
        _onboardStaged = new Rgb[_onboardZones.Length];
        _onboardSending = new Rgb[_onboardZones.Length];
        _onboardSent = new Rgb[_onboardZones.Length];
        _strips = config.Headers.Where(h => h.LedCount > 0).Select(h => new Strip(h)).ToArray();
        var zones = _strips.Select(s => new LedZoneInfo(s.Config.Header.ToString(), s.Config.Name, s.Config.LedCount)).ToList();
        zones.Add(new LedZoneInfo("onboard", "Onboard zones", _onboardZones.Length));
        Zones = zones;
    }

    public string Id { get; }
    public string Name => Info?.Product is { Length: > 0 } p ? $"Gigabyte {p}" : "Gigabyte Fusion 2";
    public DeviceKind Kind => DeviceKind.Motherboard;
    public IReadOnlyList<LedZoneInfo> Zones { get; }
    public Fusion2Info? Info { get; private set; }

    public static IEnumerable<Fusion2Device> Discover(Fusion2Config config)
    {
        foreach (var pid in P.Pids)
        {
            var dev = DeviceList.Local.GetHidDevices(P.Vid, pid)
                .FirstOrDefault(d => d.GetMaxFeatureReportLength() >= P.ReportSize);
            if (dev is not null)
                yield return new Fusion2Device(new HidSharpTransport(dev), (ushort)pid, config);
        }
    }

    public Fusion2Info ReadInfo()
    {
        lock (_ioLock)
        {
            P.Cc(_buf, P.CmdInfo);
            _io.SetFeature(_buf);
            _buf.AsSpan().Clear();
            _buf[0] = P.ReportId;
            _io.GetFeature(_buf);
            var info = P.ParseInfo(_buf);
            if (_pid == P.PidIt5711)
            {
                P.Cc(_buf, P.CmdCalibration5711);
                _io.SetFeature(_buf);
                _buf.AsSpan().Clear();
                _buf[0] = P.ReportId;
                _io.GetFeature(_buf);
                P.ParseCalibration5711(_buf, info.Calibration);
            }
            return Info = info;
        }
    }

    public void Open()
    {
        var info = ReadInfo();
        // Seen on IT5711 after Gigabyte's Dynamic Lighting component was stopped: the chip answers with an empty
        // info block and ignores every color write until a full power-off (PSU off), since it runs on standby power.
        if (info.Firmware == "0.0.0.0" && info.Product.Length == 0)
            throw new IOException("Fusion 2 controller returned an empty info report and will ignore color writes. Switch the PSU off for 30 s, then boot.");
        lock (_ioLock)
        {
            if (_forceLampArrayOff || (info.SupportFlags & 0x02) != 0) Send(P.CmdLampArray, 0x00);
            for (byte z = 0x20; z <= 0x27; z++) Send(z);
            if (_pid == P.PidIt5711)
                for (byte z = 0x90; z <= 0x92; z++) Send(z);
            Send(P.CmdApply, 0xFF, P.ApplyMask(_pid));
            Send(P.CmdBeat, 0x00);

            // Keep the board's current bucket for headers we do not drive.
            Span<byte> buckets = stackalloc byte[6];
            info.CountNibbles.CopyTo(buckets);
            byte disable = 0;
            foreach (var s in _strips)
            {
                int slot = P.HeaderSlot(s.Config.Header);
                buckets[slot] = P.CountBucket(s.Config.LedCount);
                disable |= P.BuiltinDisableBit(s.Config.Header);
                s.Order = ResolveOrder(s.Config.ColorOrder, info.Calibration[slot]);
            }
            P.LedCounts(_buf, buckets);
            _io.SetFeature(_buf);
            Send(P.CmdDisableBuiltin, disable);
            Thread.Sleep(50);
        }
        _onboardValid = false;
        _sinceFull.Restart();
        ForceFull();
    }

    /// <summary>Auto uses the board calibration; an unusable value falls back to GRB, the WS2812 default.</summary>
    public static ColorOrder ResolveOrder(ColorOrderSetting setting, uint calibration)
    {
        if (setting != ColorOrderSetting.Auto) return ColorOrder.FromSetting(setting);
        var o = ColorOrder.FromCalibration(calibration);
        return o.IsValid ? o : ColorOrder.Grb;
    }

    public void SetZone(int zoneIndex, ReadOnlySpan<Rgb> colors)
    {
        lock (_stage)
        {
            var dst = zoneIndex < _strips.Length ? _strips[zoneIndex].Staged : _onboardStaged;
            colors[..Math.Min(colors.Length, dst.Length)].CopyTo(dst);
        }
    }

    /// <summary>Safe to call from several threads: a keep-alive thread and the render worker may both flush.</summary>
    public void Flush()
    {
        lock (_ioLock)
        {
            lock (_stage)
            {
                foreach (var s in _strips) s.Staged.CopyTo(s.Sending, 0);
                _onboardStaged.CopyTo(_onboardSending, 0);
            }

            // The board drops back to its built-in effect when it gets no direct data, so resend every strip each second.
            if (_sinceFull.ElapsedMilliseconds >= FullRefreshMs)
            {
                ForceFull();
                _sinceFull.Restart();
            }

            foreach (var s in _strips)
            {
                for (int first = 0; first < s.Sending.Length; first += P.MaxLedsPerPacket)
                {
                    int n = Math.Min(P.MaxLedsPerPacket, s.Sending.Length - first);
                    var chunk = s.Sending.AsSpan(first, n);
                    var sent = s.Sent.AsSpan(first, n);
                    if (chunk.SequenceEqual(sent)) continue;
                    P.RgbPacket(_buf, s.Command, first, chunk, s.Order);
                    _io.SetFeature(_buf);
                    chunk.CopyTo(sent);
                }
            }

            if (!_onboardValid || !_onboardSending.AsSpan().SequenceEqual(_onboardSent))
            {
                for (int i = 0; i < _onboardZones.Length; i++)
                {
                    if (_onboardValid && _onboardSending[i] == _onboardSent[i]) continue;
                    P.StaticZone(_buf, _onboardZones[i], _onboardSending[i]);
                    _io.SetFeature(_buf);
                }
                Send(P.CmdApply, 0xFF, P.ApplyMask(_pid));
                _onboardSending.CopyTo(_onboardSent, 0);
                _onboardValid = true;
            }
        }
    }

    /// <summary>Invalidates the sent cache so the next flush rewrites every strip packet.</summary>
    private void ForceFull()
    {
        foreach (var s in _strips)
            for (int i = 0; i < s.Sent.Length; i++)
                s.Sent[i] = new Rgb((byte)~s.Sending[i].R, s.Sending[i].G, s.Sending[i].B);
    }

    /// <summary>Re-enables the firmware's own effects. Unverified on IT5711: may need an effect re-applied.</summary>
    public void Release()
    {
        lock (_ioLock)
        {
            Send(P.CmdDisableBuiltin, 0x00);
            Send(P.CmdApply, 0xFF, P.ApplyMask(_pid));
        }
    }

    private void Send(byte a, byte b = 0, byte c = 0)
    {
        P.Cc(_buf, a, b, c);
        _io.SetFeature(_buf);
    }

    public void Dispose() => _io.Dispose();
}
