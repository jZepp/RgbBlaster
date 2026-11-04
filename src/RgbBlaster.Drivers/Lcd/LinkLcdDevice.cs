using HidSharp;
using RgbBlaster.Drivers.Hid;
using P = RgbBlaster.Drivers.Lcd.LinkLcdProtocol;

namespace RgbBlaster.Drivers.Lcd;

/// <summary>
/// iCUE LINK AIO LCD. <see cref="ShowJpeg"/> sends one frame. The screen keeps showing it, but the host is expected to
/// refresh about once per second. <see cref="Release"/> returns the screen to its own content.
/// </summary>
public sealed class LinkLcdDevice : IDisposable
{
    private readonly IHidTransport _io;
    private readonly byte[] _buf = new byte[P.ReportLength];
    private readonly object _lock = new();

    public LinkLcdDevice(IHidTransport io, string serial)
    {
        _io = io;
        Serial = serial;
    }

    public string Serial { get; }

    public static IEnumerable<LinkLcdDevice> Discover()
    {
        foreach (var d in DeviceList.Local.GetHidDevices(P.Vid, P.PidAioLcd))
            if (d.GetMaxOutputReportLength() == P.ReportLength)
            {
                string serial;
                try { serial = d.GetSerialNumber(); } catch { serial = ""; }
                yield return new LinkLcdDevice(new HidSharpTransport(d), serial);
            }
    }

    public void ShowJpeg(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length > P.MaxChunk * 255) throw new ArgumentException("Frame is too large for the chunk index.", nameof(jpeg));
        lock (_lock)
        {
            int index = 0;
            for (int off = 0; off < jpeg.Length; off += P.MaxChunk, index++)
            {
                var chunk = jpeg.Slice(off, Math.Min(P.MaxChunk, jpeg.Length - off));
                // The screen draws the frame when it gets the chunk with the last flag set.
                P.FrameChunk(_buf, index, chunk, off + chunk.Length >= jpeg.Length);
                _io.Write(_buf);
            }
        }
    }

    /// <summary>0-100. 0 turns the backlight off.</summary>
    public void SetBrightness(byte percent)
    {
        lock (_lock)
        {
            _io.SetFeature(P.Feature(P.CmdBrightness, Math.Min(percent, (byte)100)));
            if (percent == 0) _io.SetFeature(P.Feature(P.CmdPower, 0));
        }
    }

    /// <summary>0-3, in 90-degree steps.</summary>
    public void SetRotation(byte quarterTurns)
    {
        lock (_lock) _io.SetFeature(P.Feature(P.CmdRotation, (byte)(quarterTurns & 3)));
    }

    public void Release()
    {
        lock (_lock)
        {
            _io.SetFeature(P.Feature(P.CmdRelease1, 1));
            _io.SetFeature(P.Feature(P.CmdRelease2, 0));
        }
    }

    public void Dispose() => _io.Dispose();
}
