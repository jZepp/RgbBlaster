using System.Diagnostics;
using HidSharp;
using RgbBlaster.Core;
using RgbBlaster.Drivers.Hid;
using P = RgbBlaster.Drivers.EVision.EVisionProtocol;

namespace RgbBlaster.Drivers.EVision;

/// <summary>
/// Lighting for EVision keyboards (Redragon K580). The keyboard has no streaming mode: it stores every custom-color and
/// config write and briefly blanks all lighting while it does. So:
/// <list type="bullet">
/// <item>Effects with a firmware equivalent (<see cref="INativeEffectDevice"/>) run in firmware; frames are ignored.</item>
/// <item>Otherwise key colors are written only after the frame holds for <see cref="KeySettleMs"/>, so only static colors show.</item>
/// <item>The side bar (zone 1) shows one of 8 stored palette colors. A new entry is written after it holds for
/// <see cref="SideSettleMs"/>; a color that keeps changing hands the bar to its built-in wave.</item>
/// </list>
/// </summary>
public sealed class EVisionKeyboardDevice : IRgbDevice, INativeEffectDevice
{
    /// <summary>Key colors are written only after the frame stays the same for this time.</summary>
    public const int KeySettleMs = 500;
    /// <summary>A side-bar palette entry is written only after it stays the same for this time.</summary>
    public const int SideSettleMs = 2000;
    /// <summary>Entry changes within one settle window that hand the bar to its built-in wave instead of following the effect.</summary>
    public const int SideUnstableChanges = 3;
    private const int ReplyTimeoutMs = 200;
    private const int SideUnset = -3, SideWave = -2, SideOff = -1;

    private readonly IHidTransport _io;
    private readonly int _keySettleMs, _sideSettleMs;
    private readonly byte[] _out = new byte[P.ReportLength];
    private readonly byte[] _in = new byte[P.ReportLength + 1];
    private readonly byte[] _staged = new byte[P.ColorBufferLength];
    private readonly byte[] _pending = new byte[P.ColorBufferLength];
    private readonly byte[] _sent = new byte[P.ColorBufferLength];
    private readonly object _stage = new();
    private readonly object _ioLock = new();
    private readonly Stopwatch _pendingAge = Stopwatch.StartNew();
    private bool _sentValid;

    private NativeEffect _nativeRequested, _nativeApplied;

    private Rgb _sideStaged;
    private bool _sideStagedValid;
    private int _sideSent = SideUnset;
    private int _sideCandidate = SideUnset;
    private int _sideChanges;
    private readonly Stopwatch _candidateAge = new();
    private readonly Stopwatch _sideWindow = Stopwatch.StartNew();

    public EVisionKeyboardDevice(IHidTransport io, string name, int keySettleMs = KeySettleMs, int sideSettleMs = SideSettleMs)
    {
        _io = io;
        Name = name;
        _keySettleMs = keySettleMs;
        _sideSettleMs = sideSettleMs;
        Zones =
        [
            new LedZoneInfo("keys", "Keys", P.LedCount, P.Positions()),
            new LedZoneInfo("side", "Side bar (left edge, 8-color palette)", 1),
        ];
    }

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
            WriteMode(P.ModeCustom, P.SpeedNormal, Rgb.Black, false);
            _nativeApplied = NativeEffect.None;
        }
    }

    public void SetNativeEffect(NativeEffect effect)
    {
        lock (_stage) _nativeRequested = effect;
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
            NativeEffect native;
            lock (_stage) native = _nativeRequested;
            if (native != _nativeApplied) ApplyNative(native);
            if (native.Kind != NativeEffectKind.None) return;

            FlushSide();
            FlushKeys();
        }
    }

    /// <summary>Caller holds _ioLock.</summary>
    private void ApplyNative(NativeEffect e)
    {
        switch (e.Kind)
        {
            case NativeEffectKind.Rainbow:
                WriteMode(P.ModeColorWave, Speed(e.PeriodSeconds), Rgb.White, true);
                WriteSide(SideWave);
                break;
            case NativeEffectKind.Breathe:
                WriteMode(P.ModeBreathing, Speed(e.PeriodSeconds), e.Color, false);
                WriteSide(P.SideBarIndex(e.Color) ?? SideOff);
                break;
            default:
                WriteMode(P.ModeCustom, P.SpeedNormal, Rgb.Black, false);
                _sentValid = false;
                _sideCandidate = SideUnset;
                break;
        }
        _nativeApplied = e;
    }

    /// <summary>Firmware speed 0 (fastest) to 5 (slowest) for an effect period.</summary>
    public static byte Speed(float periodSeconds) =>
        periodSeconds <= 0 ? P.SpeedNormal : (byte)Math.Clamp((int)MathF.Ceiling(periodSeconds / 2f), 1, P.SpeedSlowest);

    /// <summary>Writes key colors after the frame has held for the settle time. Caller holds _ioLock.</summary>
    private void FlushKeys()
    {
        bool changed;
        lock (_stage)
        {
            changed = !_staged.AsSpan().SequenceEqual(_pending);
            if (changed) _staged.CopyTo(_pending, 0);
        }
        if (changed) _pendingAge.Restart();
        if (_pendingAge.ElapsedMilliseconds < _keySettleMs) return;
        if (_sentValid && _pending.AsSpan().SequenceEqual(_sent)) return;

        for (int off = 0; off < P.ColorBufferLength; off += P.MaxColorBytes)
        {
            var chunk = _pending.AsSpan(off, Math.Min(P.MaxColorBytes, P.ColorBufferLength - off));
            if (_sentValid && chunk.SequenceEqual(_sent.AsSpan(off, chunk.Length))) continue;
            P.Colors(_out, off, chunk);
            Exchange();
        }
        _pending.CopyTo(_sent, 0);
        _sentValid = true;
    }

    /// <summary>Writes the side-bar mode and palette index when the chosen entry settled. Caller holds _ioLock.</summary>
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

    /// <summary>Caller holds _ioLock.</summary>
    private void WriteMode(byte mode, byte speed, Rgb color, bool random)
    {
        P.Control(_out, P.CmdBegin);
        Exchange();
        P.ModeEx(_out, mode, P.BrightnessMax, speed, color, random);
        Exchange();
        P.Control(_out, P.CmdEnd);
        Exchange();
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

    /// <summary>The keyboard has no host mode to leave; it keeps the last mode and colors.</summary>
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
