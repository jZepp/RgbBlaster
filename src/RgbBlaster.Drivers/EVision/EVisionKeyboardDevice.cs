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
/// The side bar (zone 1) is not in the color buffer. It shows one of 8 stored palette colors. A new entry is written after it
/// holds for <see cref="SideSettleMs"/>; a color that keeps changing hands the bar to its built-in wave.
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

    public EVisionKeyboardDevice(IHidTransport io, string name, int sideSettleMs = SideSettleMs)
    {
        _io = io;
        Name = name;
        _sideSettleMs = sideSettleMs;
        Zones =
        [
            new LedZoneInfo("keys", "Keys", P.LedCount, P.Positions()),
            new LedZoneInfo("side", "Side bar (left edge, 8-color palette)", 1),
        ];
    }

    /// <summary>
    /// A side-bar palette entry is written only after it stays the same for this time. Each write is a stored config write
    /// and makes the whole keyboard reload its lighting (a visible flash).
    /// </summary>
    public const int SideSettleMs = 2000;
    /// <summary>Entry changes within one settle window that hand the bar to its built-in wave instead of following the effect.</summary>
    public const int SideUnstableChanges = 3;
    private const int SideUnset = -3, SideWave = -2, SideOff = -1;
    private readonly int _sideSettleMs;
    private Rgb _sideStaged;
    private bool _sideStagedValid;
    private int _sideSent = SideUnset;
    private int _sideCandidate = SideUnset;
    private int _sideChanges;
    private readonly Stopwatch _candidateAge = new();
    private readonly Stopwatch _sideWindow = Stopwatch.StartNew();

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
        if (target != _sideCandidate)
        {
            _sideCandidate = target;
            _candidateAge.Restart();
            _sideChanges++;
        }
        if (_candidateAge.ElapsedMilliseconds >= _sideSettleMs)
        {
            _sideChanges = 0;
            _sideWindow.Restart();
            WriteSide(_sideCandidate);
        }
        else if (_sideWindow.ElapsedMilliseconds >= _sideSettleMs)
        {
            if (_sideChanges >= SideUnstableChanges) WriteSide(SideWave);
            _sideChanges = 0;
            _sideWindow.Restart();
        }
    }

    private void WriteSide(int state)
    {
        if (state == _sideSent) return;
        if (state == SideOff) WriteConfig(P.SideBarModeAddress, [P.SideBarModeOff]);
        else if (state == SideWave) WriteConfig(P.SideBarModeAddress, [P.SideBarModeWave]);
        else WriteConfig(P.SideBarModeAddress, [P.SideBarModeStatic, 0x00, (byte)state]);
        _sideSent = state;
        _sentValid = false; // the config write reloads the keyboard's lighting; resend all keys
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
