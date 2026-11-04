using System.Buffers.Binary;
using System.Text;
using RgbBlaster.Core;
using RgbBlaster.Drivers.Fusion2;
using RgbBlaster.Drivers.Hid;
using RgbBlaster.Engine;
using P = RgbBlaster.Drivers.Fusion2.Fusion2Protocol;

namespace RgbBlaster.Tests;

internal sealed class FakeHid : IHidTransport
{
    public readonly List<byte[]> Sent = [];
    public byte[] InfoReply = new byte[64];
    public byte[] CalReply = new byte[64];
    private byte _lastCmd;

    public void SetFeature(ReadOnlySpan<byte> r) { Sent.Add(r.ToArray()); _lastCmd = r[1]; }
    public void GetFeature(Span<byte> r) => (_lastCmd == P.CmdCalibration5711 ? CalReply : InfoReply).CopyTo(r);
    public void Write(ReadOnlySpan<byte> r) => Sent.Add(r.ToArray());
    public int Read(Span<byte> r, int t) => 0;
    public void Dispose() { }
}

public class Fusion2ProtocolTests
{
    [Theory]
    [InlineData(1, 0)] [InlineData(32, 0)] [InlineData(33, 1)] [InlineData(64, 1)]
    [InlineData(65, 2)] [InlineData(256, 2)] [InlineData(512, 3)] [InlineData(513, 4)]
    public void CountBucket(int leds, byte expected) => Assert.Equal(expected, P.CountBucket(leds));

    [Fact]
    public void LedCountsPacksNibblesLowEvenHighOdd()
    {
        var b = new byte[64];
        P.LedCounts(b, [1, 2, 3, 4, 0, 2]);
        Assert.Equal([0xCC, 0x34, 0x21, 0x43, 0x20], b[..5]);
    }

    [Fact]
    public void RgbPacketLayoutAndOrder()
    {
        var b = new byte[64];
        P.RgbPacket(b, 0x59, 19, [new Rgb(1, 2, 3), new Rgb(4, 5, 6)], ColorOrder.Grb);
        Assert.Equal(0xCC, b[0]);
        Assert.Equal(0x59, b[1]);
        Assert.Equal(57, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2))); // 19 LEDs * 3 bytes
        Assert.Equal(6, b[4]);
        Assert.Equal([2, 1, 3, 5, 4, 6], b[5..11]);
        Assert.All(b[11..], x => Assert.Equal(0, x));
    }

    [Fact]
    public void RgbPacketRejectsMoreThan19() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => P.RgbPacket(new byte[64], 0x58, 0, new Rgb[20], ColorOrder.Rgb));

    [Theory]
    [InlineData(0x00000102u, 0, 1, 2)] // RGB
    [InlineData(0x00010002u, 1, 0, 2)] // GRB
    public void CalibrationDecodes(uint v, int r, int g, int b) =>
        Assert.Equal(new ColorOrder(r, g, b), ColorOrder.FromCalibration(v));

    [Fact]
    public void InvalidCalibrationFallsBackToGrb() =>
        Assert.Equal(ColorOrder.Grb, Fusion2Device.ResolveOrder(ColorOrderSetting.Auto, 0));

    [Fact]
    public void ExplicitOrderOverridesCalibration() =>
        Assert.Equal(ColorOrder.Rgb, Fusion2Device.ResolveOrder(ColorOrderSetting.RGB, 0x00010002));

    [Fact]
    public void StaticZoneHighZonesUse0x90AndBgr()
    {
        var b = new byte[64];
        P.StaticZone(b, 9, new Rgb(0x11, 0x22, 0x33));
        Assert.Equal(0x91, b[1]);
        Assert.Equal(1u << 9, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(2)));
        Assert.Equal(1, b[11]);
        Assert.Equal([0x33, 0x22, 0x11], b[14..17]);
    }

    [Fact]
    public void ParseInfo()
    {
        var r = new byte[64];
        r[0] = 0xCC; r[3] = 1; r[4] = 2; r[5] = 0; r[6] = 4; r[7] = 9;
        r[8] = 0x21; r[11] = 0x03;
        Encoding.ASCII.GetBytes("IT5711-GIGABYTE V3.2.4.9").CopyTo(r, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(44), 0x00010002);
        var i = P.ParseInfo(r);
        Assert.Equal("IT5711-GIGABYTE V3.2.4.9", i.Product);
        Assert.Equal("2.0.4.9", i.Firmware);
        Assert.Equal(1, i.CountNibbles[0]);
        Assert.Equal(2, i.CountNibbles[1]);
        Assert.Equal(0x00010002u, i.Calibration[0]);
    }
}

public class Fusion2DeviceTests
{
    private static (Fusion2Device Dev, FakeHid Hid) Make(int leds)
    {
        var hid = new FakeHid();
        var cfg = new Fusion2Config { Headers = [new() { Header = Fusion2Header.D_LED2, Name = "rear", LedCount = leds, ColorOrder = ColorOrderSetting.RGB }] };
        return (new Fusion2Device(hid, P.PidIt5711, cfg), hid);
    }

    [Fact]
    public void OpenDisablesBuiltinOnlyForDrivenHeadersAndSetsCount()
    {
        var (dev, hid) = Make(40);
        dev.Open();
        var count = hid.Sent.Single(p => p[1] == P.CmdLedCount);
        Assert.Equal(0x10, count[2]); // D_LED2 is the high nibble of byte 2; 40 LEDs -> bucket 1
        var disable = hid.Sent.Single(p => p[1] == P.CmdDisableBuiltin);
        Assert.Equal(0x02, disable[2]);
    }

    [Fact]
    public void FlushSendsOnlyChangedChunks()
    {
        var (dev, hid) = Make(40); // 3 chunks: 19, 19, 2
        dev.Open();
        var frame = new Rgb[40];
        dev.SetZone(0, frame);
        dev.Flush();
        Assert.Equal(3, hid.Sent.Count(p => p[1] == 0x59));

        hid.Sent.Clear();
        dev.Flush();
        Assert.Empty(hid.Sent);

        frame[20] = Rgb.White; // second chunk only
        dev.SetZone(0, frame);
        dev.Flush();
        var pkt = Assert.Single(hid.Sent);
        Assert.Equal(19 * 3, BinaryPrimitives.ReadUInt16LittleEndian(pkt.AsSpan(2)));
    }

    [Fact]
    public void OnboardChangeSendsEffectThenApply()
    {
        var (dev, hid) = Make(8);
        dev.Open();
        dev.Flush();
        hid.Sent.Clear();
        var onboard = new Rgb[P.OnboardZones.Length];
        onboard[0] = Rgb.White;
        dev.SetZone(1, onboard);
        dev.Flush();
        Assert.Equal(2, hid.Sent.Count);
        Assert.Equal(0x20, hid.Sent[0][1]);
        Assert.Equal([0xCC, 0x28, 0xFF, 0x07], hid.Sent[1][..4]);
    }
}

public class EngineTests
{
    [Fact]
    public void RingMapsEveryLedInsideCanvas()
    {
        var idx = LayoutMapper.Map(new ZonePlacement { Shape = PlacementShape.Ring, X = 0.1f, Y = 0.2f, Radius = 0.05f, Fans = 3, FanDy = 0.3f }, 30, 320, 200);
        Assert.Equal(30, idx.Length);
        Assert.All(idx, i => Assert.InRange(i, 0, 320 * 200 - 1));
        Assert.True(idx[0] / 320 < idx[10] / 320); // fan 2 sits below fan 1
    }

    [Fact]
    public void LineEndpoints()
    {
        var idx = LayoutMapper.Map(new ZonePlacement { Shape = PlacementShape.Line, X = 0, Y = 0, X2 = 1, Y2 = 0 }, 5, 100, 10);
        Assert.Equal(0, idx[0]);
        Assert.Equal(99, idx[4]);
    }

    [Fact]
    public void HsvPrimaries()
    {
        Assert.Equal(new Rgb(255, 0, 0), Rgb.FromHsv(0, 1, 1));
        Assert.Equal(new Rgb(0, 255, 0), Rgb.FromHsv(120, 1, 1));
        Assert.Equal(new Rgb(0, 0, 255), Rgb.FromHsv(240, 1, 1));
    }

    [Fact]
    public void RainbowFrameDoesNotAllocateAfterWarmup()
    {
        var c = new Canvas(320, 200);
        var e = new RainbowWaveEffect();
        e.Render(c, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) e.Render(c, i / 60.0);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
