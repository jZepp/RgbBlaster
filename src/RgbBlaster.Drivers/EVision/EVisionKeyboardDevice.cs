using System.Diagnostics;
using HidSharp;
using RgbBlaster.Core;
using RgbBlaster.Drivers.Hid;
using P = RgbBlaster.Drivers.EVision.EVisionProtocol;

namespace RgbBlaster.Drivers.EVision;

/// <summary>
/// Per-key lighting for EVision keyboards (Redragon K580). Open switches the keyboard to its custom-color mode;
/// Flush rewrites the 54-byte color packets that changed. The keyboard keeps custom colors after the app exits.
/// It is unknown whether the keyboard writes custom colors to flash, so updates are limited to <see cref="MaxRateHz"/>.
/// The side bar (zone 1) is not in the color buffer. It shows one of 8 stored palette colors, so it changes only when the
/// nearest palette entry changes, at most once per <see cref="SideMinIntervalMs"/>.
/// </summary>
public sealed class EVisionKeyboardDevice : IRgbDevice
{
    public const int MaxRateHz = 20;
    private const int ReplyTimeoutMs = 200;

    private readonly IHidTransport _io;
    private readonly byte[] _out = new byte[P.ReportLength];
    private readonly byte[] _in = new byte[P.ReportLength + 1];
    private readonly byte[] _staged = new byte[P.ColorBufferLength];
    private readonly byte[] _sending = new byte[P.ColorBufferLength];
    private readonly byte[] _sent = new byte[P.ColorBufferLength];
    private readonly object _stage = new();
    private readonly object _ioLock = new();
    private readonly Stopwatch _sinceSend = new();
    private bool _sentValid;

    public EVisionKeyboardDevice(IHidTransport io, string name)
    {
        _io = io;
        Name = name;
        Zones =
        [
            new LedZoneInfo("keys", "Keys", P.LedCount, P.Positions()),
            new LedZoneInfo("side", "Side bar (left edge, 8-color palette)", 1),
        ];
    }

    /// <summary>Side-bar changes are stored config writes, so they are limited to one per this interval.</summary>
    public const int SideMinIntervalMs = 500;
    private const int SideUnset = -2, SideOff = -1;
    private Rgb _sideStaged;
    private bool _sideStagedValid;
    private int _sideSent = SideUnset;
    private readonly Stopwatch _sinceSide = new();

    public string Id => "keyboard";
    public string Name { get; }
    public DeviceKind Kind => DeviceKind.Keyboard;
    public IReadOnlyList<LedZoneInfo> Zones { get; }

    public static IEnumerable<EVisionKeyboardDevice> Discover()
    {
        foreach (var d in DeviceList.Local.GetHidDevices(P.Vid, P.PidK580))
            if (d.GetMaxOutputReportLength() == P.ReportLength)
                yield return new EVisionKeyboardDevice(new HidSharpTransport(d), "Redragon K580");
    }

    public void Open()
    {
        lock (_ioLock)
        {
            P.Control(_out, P.CmdBegin);
            Exchange();
            P.ModeEx(_out, P.ModeCustom, P.BrightnessMax, P.SpeedNormal, Rgb.Black);
            Exchange();
            P.Control(_out, P.CmdEnd);
            Exchange();
            _sentValid = false;
            _sinceSend.Restart();
        }
    }

    public void SetZone(int zoneIndex, ReadOnlySpan<Rgb> colors)
    {
        lock (_stage)
        {
            if (zoneIndex == 1)
            {
                if (colors.Length > 0) { _sideStaged = colors[0]; _sideStagedValid = true; }
                return;
            }
            if (zoneIndex != 0) return;
            int n = Math.Min(colors.Length, P.LedCount);
            for (int i = 0; i < n; i++)
            {
                _staged[3 * i] = colors[i].R;
                _staged[3 * i + 1] = colors[i].G;
                _staged[3 * i + 2] = colors[i].B;
            }
        }
    }

    public void Flush()
    {
        lock (_ioLock)
        {
            FlushSide();
            if (_sentValid && _sinceSend.ElapsedMilliseconds < 1000 / MaxRateHz) return;
            lock (_stage) _staged.CopyTo(_sending, 0);
            if (_sentValid && _sending.AsSpan().SequenceEqual(_sent)) return;

            for (int off = 0; off < P.ColorBufferLength; off += P.MaxColorBytes)
            {
                var chunk = _sending.AsSpan(off, Math.Min(P.MaxColorBytes, P.ColorBufferLength - off));
                if (_sentValid && chunk.SequenceEqual(_sent.AsSpan(off, chunk.Length))) continue;
                P.Colors(_out, off, chunk);
                Exchange();
            }
            _sending.CopyTo(_sent, 0);
            _sentValid = true;
            _sinceSend.Restart();
        }
    }

    /// <summary>Writes the side-bar mode and palette index when the chosen entry changed. Caller holds _ioLock.</summary>
    private void FlushSide()
    {
        Rgb c;
        lock (_stage)
        {
            if (!_sideStagedValid) return;
            c = _sideStaged;
        }
        int target = P.SideBarIndex(c) ?? SideOff;
        if (target == _sideSent) return;
        if (_sideSent != SideUnset && _sinceSide.ElapsedMilliseconds < SideMinIntervalMs) return;
        if (target == SideOff) WriteConfig(P.SideBarModeAddress, [P.SideBarModeOff]);
        else WriteConfig(P.SideBarModeAddress, [P.SideBarModeStatic, 0x00, (byte)target]);
        _sideSent = target;
        _sinceSide.Restart();
    }

    /// <summary>Diagnostic, read-only: returns the full reply to a config read (begin, read, end).</summary>
    public byte[] ReadConfig(byte address, byte length)
    {
        lock (_ioLock)
        {
            P.Control(_out, P.CmdBegin);
            Exchange();
            P.ReadParameter(_out, address, length);
            _io.Write(_out);
            int n = _io.Read(_in, 500);
            var reply = _in.AsSpan(0, n).ToArray();
            P.Control(_out, P.CmdEnd);
            Exchange();
            return reply;
        }
    }

    /// <summary>Diagnostic: writes raw config bytes (begin, write, end). The keyboard keeps them.</summary>
    public void WriteConfig(byte address, ReadOnlySpan<byte> data)
    {
        lock (_ioLock)
        {
            P.Control(_out, P.CmdBegin);
            Exchange();
            P.WriteParameter(_out, address, data);
            Exchange();
            P.Control(_out, P.CmdEnd);
            Exchange();
        }
    }

    /// <summary>The keyboard has no host mode to leave; it keeps the last custom colors.</summary>
    public void Release() { }

    public void Dispose() => _io.Dispose();

    /// <summary>Writes _out and reads its reply. The reply content is not used. Caller holds _ioLock.</summary>
    private void Exchange()
    {
        _io.Write(_out);
        try { _io.Read(_in, ReplyTimeoutMs); }
        catch (TimeoutException) { /* some firmware does not answer every packet */ }
    }
}
