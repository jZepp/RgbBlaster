using RgbBlaster.Core;
using RgbBlaster.Drivers.EVision;
using RgbBlaster.Drivers.Hid;
using RgbBlaster.Engine;
using P = RgbBlaster.Drivers.EVision.EVisionProtocol;

namespace RgbBlaster.Tests;

internal sealed class FakeEVision : IHidTransport
{
    public readonly List<byte[]> Writes = [];
    public void Write(ReadOnlySpan<byte> r) => Writes.Add(r.ToArray());
    public int Read(Span<byte> r, int t) => 64;
    public void SetFeature(ReadOnlySpan<byte> r) { }
    public void GetFeature(Span<byte> r) { }
    public void Dispose() { }
}

public class EVisionTests
{
    private static ushort Sum(byte[] p) { ushort s = 0; for (int i = 3; i < 64; i++) s += p[i]; return s; }

    [Fact]
    public void OpenEntersCustomModeBetweenBeginAndEnd()
    {
        var hw = new FakeEVision();
        new EVisionKeyboardDevice(hw, "K580").Open();
        Assert.Equal(3, hw.Writes.Count);
        Assert.Equal([P.ReportId, P.CmdBegin, 0, P.CmdBegin], hw.Writes[0][..4]);
        var mode = hw.Writes[1];
        Assert.Equal([P.CmdSetParameter, 8, 0], mode[3..6]);
        Assert.Equal(P.ModeCustom, mode[8]);
        Assert.Equal(P.BrightnessMax, mode[9]);
        Assert.Equal(Sum(mode), (ushort)(mode[1] | mode[2] << 8));
        Assert.Equal(P.CmdEnd, hw.Writes[2][3]);
    }

    [Fact]
    public void FlushWritesSevenChecksummedColorPacketsThenOnlyChangedOnes()
    {
        var hw = new FakeEVision();
        var dev = new EVisionKeyboardDevice(hw, "K580");
        dev.Open();
        hw.Writes.Clear();
        var colors = new Rgb[P.LedCount];
        colors[20] = new Rgb(200, 100, 50); // byte 60: second packet
        dev.SetZone(0, colors);
        dev.Flush();
        Assert.Equal(7, hw.Writes.Count);
        var second = hw.Writes[1];
        Assert.Equal([P.CmdWriteColors, 54, 54, 0], second[3..7]);
        Assert.Equal([200, 100, 50], second[(8 + 6)..(8 + 9)]);
        Assert.All(hw.Writes, w => Assert.Equal(Sum(w), (ushort)(w[1] | w[2] << 8)));

        Thread.Sleep(1000 / EVisionKeyboardDevice.MaxRateHz + 10);
        hw.Writes.Clear();
        colors[125] = Rgb.White; // last packet only
        dev.SetZone(0, colors);
        dev.Flush();
        Assert.Equal(6 * 54, hw.Writes.Single()[5] | hw.Writes.Single()[6] << 8);
    }

    [Fact]
    public void GridPlacementUsesMatrixPositions()
    {
        var pos = P.Positions();
        Assert.True(pos[0].X < pos[16].X);   // Esc left of the last top-row key
        Assert.True(pos[0].Y < pos[105].Y);  // top row above bottom row
        var idx = LayoutMapper.Map(new ZonePlacement { Shape = PlacementShape.Grid, X = 0, Y = 0, X2 = 1, Y2 = 1 }, P.LedCount, 230, 60, pos);
        Assert.NotEqual(idx[0], idx[105]);
    }
}
