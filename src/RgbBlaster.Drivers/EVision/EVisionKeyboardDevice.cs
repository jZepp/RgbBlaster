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
        Zones = [new LedZoneInfo("keys", "Keys", P.LedCount, P.Positions())];
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
