using RgbBlaster.Core;
using RgbBlaster.Drivers.Hid;
using RgbBlaster.Drivers.Logitech;
using P = RgbBlaster.Drivers.Logitech.HidppProtocol;

namespace RgbBlaster.Tests;

/// <summary>HID++ mouse with feature 0x8070 at index 7 and two zones (primary, logo); fixed effect is index 1 in each.</summary>
internal sealed class FakeHidppMouse : IHidTransport
{
    public const byte LedIndex = 7;
    public readonly List<byte[]> Requests = [];
    private readonly Queue<byte[]> _replies = new();
    public bool Has8070 = true;

    public void Write(ReadOnlySpan<byte> r)
    {
        var req = r.ToArray();
        Requests.Add(req);
        var reply = (byte[])req.Clone();
        byte fn = (byte)(req[3] >> 4);
        var p = reply.AsSpan(P.ParamOffset);
        if (req[2] == P.RootIndex) { p.Clear(); p[0] = Has8070 ? LedIndex : (byte)0; }
        else if (req[2] == LedIndex)
        {
            switch (fn)
            {
                case P.FnGetInfo: p.Clear(); p[0] = 2; break;
                case P.FnGetZoneInfo: byte z = req[4]; p.Clear(); p[0] = z; p[2] = (byte)(z + 1); p[3] = 3; break;
                case P.FnGetEffectInfo: byte e = req[5]; p[2] = 0; p[3] = e == 1 ? (byte)0x01 : (byte)0x03; break;
            }
        }
        // A notification first, to check that unrelated traffic is skipped.
        _replies.Enqueue([P.ReportLong, P.WiredDevice, 0x04, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        _replies.Enqueue(reply);
    }

    public int Read(Span<byte> r, int t)
    {
        if (_replies.Count == 0) throw new TimeoutException();
        var x = _replies.Dequeue();
        x.CopyTo(r);
        return x.Length;
    }

    public void SetFeature(ReadOnlySpan<byte> r) { }
    public void GetFeature(Span<byte> r) { }
    public void Dispose() { }

    public IEnumerable<byte[]> Calls(byte fn) => Requests.Where(q => q[2] == LedIndex && q[3] >> 4 == fn);
}

public class LogitechTests
{
    [Fact]
    public void OpenFindsZonesFixedEffectAndTakesSoftwareControl()
    {
        var hw = new FakeHidppMouse();
        var dev = new LogitechMouseDevice(hw, "G502");
        dev.Open();
        Assert.Equal(["Primary", "Logo"], dev.Zones.Select(z => z.Name));
        Assert.Equal([0x80, 0x70], hw.Requests[0][4..6]);                  // root lookup of 0x8070
        Assert.Equal([1, 1], hw.Calls(P.FnSoftwareControl).Single()[4..6]);
        Assert.All(hw.Requests, q => Assert.Equal(P.SoftwareId, q[3] & 0x0F));
    }

    [Fact]
    public void FlushSetsFixedEffectColorOnlyWhenChanged()
    {
        var hw = new FakeHidppMouse();
        var dev = new LogitechMouseDevice(hw, "G502");
        dev.Open();
        dev.SetZone(1, [new Rgb(10, 20, 30)]);
        dev.Flush();
        var set = hw.Calls(P.FnSetZoneEffect).ToList();
        Assert.Equal(2, set.Count);
        Assert.Equal([1, 1, 10, 20, 30], set[1][4..9]);                    // zone 1, fixed effect index 1, RGB
        Assert.Equal(0, set[1][4 + 12]);                                   // not persisted

        hw.Requests.Clear();
        dev.Flush();
        Assert.Empty(hw.Calls(P.FnSetZoneEffect));
    }

    [Fact]
    public void ReleaseHandsLightingBackAndMissingFeatureFails()
    {
        var hw = new FakeHidppMouse();
        var dev = new LogitechMouseDevice(hw, "G502");
        dev.Open();
        hw.Requests.Clear();
        dev.Release();
        Assert.Equal([0, 0], hw.Calls(P.FnSoftwareControl).Single()[4..6]);

        Assert.Throws<IOException>(new LogitechMouseDevice(new FakeHidppMouse { Has8070 = false }, "x").Open);
    }
}
