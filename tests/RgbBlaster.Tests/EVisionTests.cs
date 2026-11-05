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

    /// <summary>Opened keyboard after its first Flush (custom mode written), with the write log cleared.</summary>
    private static EVisionKeyboardDevice Opened(FakeEVision hw, int keySettleMs = 0, int sideSettleMs = 0, int storeIntervalMs = 0)
    {
        var dev = new EVisionKeyboardDevice(hw, "K580", keySettleMs, sideSettleMs, storeIntervalMs);
        dev.Open();
        dev.Flush();
        hw.Writes.Clear();
        return dev;
    }

    [Fact]
    public void OpenWritesNothingAndFirstFlushEntersCustomMode()
    {
        var hw = new FakeEVision();
        var dev = new EVisionKeyboardDevice(hw, "K580");
        dev.Open();
        Assert.Empty(hw.Writes);
        dev.Flush();
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
    public void KeysAreWrittenOnlyAfterTheFrameSettlesThenOnlyChangedPackets()
    {
        var hw = new FakeEVision();
        var dev = Opened(hw, keySettleMs: 50);
        var colors = new Rgb[P.LedCount];
        colors[20] = new Rgb(200, 100, 50); // byte 60: second packet
        dev.SetZone(0, colors);
        dev.Flush();
        Assert.Empty(hw.Writes);                 // frame just changed: not settled

        Thread.Sleep(60);
        dev.Flush();
        Assert.Equal(7, hw.Writes.Count);
        var second = hw.Writes[1];
        Assert.Equal([P.CmdWriteColors, 54, 54, 0], second[3..7]);
        Assert.Equal([200, 100, 50], second[(8 + 6)..(8 + 9)]);
        Assert.All(hw.Writes, w => Assert.Equal(Sum(w), (ushort)(w[1] | w[2] << 8)));

        hw.Writes.Clear();
        colors[125] = Rgb.White; // last packet only
        dev.SetZone(0, colors);
        dev.Flush();
        Thread.Sleep(60);
        dev.Flush();
        Assert.Equal(6 * 54, hw.Writes.Single()[5] | hw.Writes.Single()[6] << 8);
    }

    [Fact]
    public void NativeRainbowSetsFirmwareWaveOnceAndIgnoresFrames()
    {
        var hw = new FakeEVision();
        var dev = Opened(hw);
        dev.SetNativeEffect(new NativeEffect(NativeEffectKind.Rainbow, PeriodSeconds: 5));
        dev.SetZone(0, Enumerable.Repeat(Rgb.White, P.LedCount).ToArray());
        dev.SetZone(1, [Rgb.White]);
        dev.Flush();
        dev.Flush();
        var mode = hw.Writes.Where(w => w[3] == P.CmdSetParameter && w[5] == 0).ToList();
        Assert.Equal(P.ModeColorWave, mode.Single()[8]);
        Assert.Equal(1, mode.Single()[12]);                                   // random colors = rainbow
        var side = hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == P.SideBarModeAddress);
        Assert.Equal(P.SideBarModeWave, side[8]);
        Assert.DoesNotContain(hw.Writes, w => w[3] == P.CmdWriteColors);

        hw.Writes.Clear();
        dev.SetNativeEffect(NativeEffect.None);                               // back to frames
        dev.Flush();
        dev.Flush();
        Assert.Equal(P.ModeCustom, hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == 0)[8]);
        Assert.Equal(7, hw.Writes.Count(w => w[3] == P.CmdWriteColors));
    }

    [Fact]
    public void NativeBreatheUsesColorAndNearestSidePaletteEntry()
    {
        var hw = new FakeEVision();
        var dev = Opened(hw);
        dev.SetNativeEffect(new NativeEffect(NativeEffectKind.Breathe, new Rgb(0, 0, 255), 4));
        dev.Flush();
        dev.Flush();
        var mode = hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == 0);
        Assert.Equal([P.ModeBreathing, P.BrightnessMax, EVisionKeyboardDevice.Speed(4), 0, 0, 0, 0, 255], mode[8..16]);
        var side = hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == P.SideBarModeAddress);
        Assert.Equal([P.SideBarModeStatic, 0, 5], side[8..11]);
    }

    [Fact]
    public void NativeCycleUsesSpectrumCycleAndSideBarCycle()
    {
        var hw = new FakeEVision();
        var dev = Opened(hw);
        dev.SetNativeEffect(new NativeEffect(NativeEffectKind.Cycle, PeriodSeconds: 5));
        dev.Flush();
        dev.Flush();
        var mode = hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == 0);
        Assert.Equal(P.ModeSpectrumCycle, mode[8]);
        var side = hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == P.SideBarModeAddress);
        Assert.Equal(P.SideBarModeCycle, side[8]);
    }

    [Fact]
    public void FirmwareModeOverridesTheEffectUntilCleared()
    {
        var hw = new FakeEVision();
        var dev = Opened(hw);
        dev.SetNativeEffect(new NativeEffect(NativeEffectKind.Rainbow, PeriodSeconds: 5));
        dev.SetFirmwareMode(0x0D, new Rgb(0, 0, 255), 2, false, direction: 3);
        dev.Flush();
        dev.Flush();
        var mode = hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == 0);
        Assert.Equal([0x0D, P.BrightnessMax, 2, 3, 0, 0, 0, 255], mode[8..16]);   // byte 11 = direction

        hw.Writes.Clear();
        dev.SetFirmwareMode(null, default, 0, false);
        dev.Flush();
        Assert.Equal(P.ModeColorWave, hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == 0)[8]);
    }

    [Fact]
    public void NativeWaveUsesSingleColorFirmwareWave()
    {
        var hw = new FakeEVision();
        var dev = Opened(hw);
        dev.SetNativeEffect(new NativeEffect(NativeEffectKind.Wave, new Rgb(0, 0, 255), 4));
        dev.Flush();
        dev.Flush();
        var mode = hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == 0);
        Assert.Equal([P.ModeColorWave, P.BrightnessMax, EVisionKeyboardDevice.Speed(4), 0, 0, 0, 0, 255], mode[8..16]);
        var side = hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == P.SideBarModeAddress);
        Assert.Equal([P.SideBarModeStatic, 0, 5], side[8..11]);
    }

    [Theory]
    [InlineData(0f, 0x02, 0)]
    [InlineData(180f, 0x02, 1)]
    [InlineData(200f, 0x02, 1)]
    [InlineData(91f, 0x0C, 0)]
    [InlineData(270f, 0x0C, 0)]
    public void NativeWavePicksFirmwareModeAndDirectionFromAngle(float angle, byte mode, byte direction)
    {
        var hw = new FakeEVision();
        var dev = Opened(hw);
        dev.SetNativeEffect(new NativeEffect(NativeEffectKind.Wave, new Rgb(0, 255, 0), 4, AngleDegrees: angle));
        dev.Flush();
        var m = hw.Writes.Single(w => w[3] == P.CmdSetParameter && w[5] == 0);
        Assert.Equal(mode, m[8]);
        Assert.Equal(direction, m[11]);
    }

    [Fact]
    public void SideBarIndexPicksNearestPaletteEntry()
    {
        Assert.Null(P.SideBarIndex(Rgb.Black));
        Assert.Equal(7, P.SideBarIndex(Rgb.White));
        Assert.Equal(7, P.SideBarIndex(new Rgb(120, 120, 110)));
        Assert.Equal(0, P.SideBarIndex(new Rgb(255, 10, 0)));
        Assert.Equal(1, P.SideBarIndex(new Rgb(255, 140, 0)));
        Assert.Equal(3, P.SideBarIndex(new Rgb(0, 200, 30)));
        Assert.Equal(5, P.SideBarIndex(new Rgb(0, 0, 255)));
        Assert.Equal(6, P.SideBarIndex(new Rgb(200, 0, 255)));
        Assert.Equal(0, P.SideBarIndex(new Rgb(255, 0, 40)));   // hue wraps past 360 to red
    }

    [Fact]
    public void SideBarWritesSettledEntryOnceAndResendsKeys()
    {
        var hw = new FakeEVision();
        var dev = Opened(hw, sideSettleMs: 50);
        dev.SetZone(1, [new Rgb(0, 0, 255)]);
        dev.Flush();
        hw.Writes.Clear();
        dev.Flush();
        Assert.DoesNotContain(hw.Writes, w => w[3] == P.CmdSetParameter);  // not settled yet

        Thread.Sleep(60);
        hw.Writes.Clear();
        dev.Flush();
        var side = hw.Writes.Single(w => w[3] == P.CmdSetParameter);
        Assert.Equal([3, P.SideBarModeAddress], side[4..6]);
        Assert.Equal([P.SideBarModeStatic, 0, 5], side[8..11]);
        Assert.Equal(7, hw.Writes.Count(w => w[3] == P.CmdWriteColors));     // keys resent after the reload

        Thread.Sleep(60);
        hw.Writes.Clear();
        dev.SetZone(1, [new Rgb(10, 10, 250)]);  // same entry
        dev.Flush();
        Assert.DoesNotContain(hw.Writes, w => w[3] == P.CmdSetParameter);
    }

    [Fact]
    public void ChangingSideColorHandsBarToBuiltInWave()
    {
        var hw = new FakeEVision();
        var dev = Opened(hw, sideSettleMs: 200);
        Rgb[] seq = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 0, 0)];
        foreach (var c in seq) { dev.SetZone(1, [c]); dev.Flush(); Thread.Sleep(30); }
        Thread.Sleep(100);
        dev.SetZone(1, [new Rgb(0, 255, 0)]);
        dev.Flush();
        var side = hw.Writes.Where(w => w[3] == P.CmdSetParameter).ToList();
        Assert.Equal([P.SideBarModeWave], side.Single()[8..9]);
    }

    [Fact]
    public void StoredWritesWaitForTheCapInterval()
    {
        var hw = new FakeEVision();
        var dev = Opened(hw, storeIntervalMs: 200);              // custom mode was just stored
        dev.SetZone(0, Enumerable.Repeat(Rgb.White, P.LedCount).ToArray());
        dev.Flush();
        Assert.Empty(hw.Writes);                                 // within the cap: keys wait

        Thread.Sleep(210);
        dev.Flush();
        Assert.Equal(7, hw.Writes.Count(w => w[3] == P.CmdWriteColors));

        hw.Writes.Clear();
        dev.SetNativeEffect(new NativeEffect(NativeEffectKind.Rainbow, PeriodSeconds: 5));
        dev.Flush();
        Assert.Empty(hw.Writes);                                 // mode change waits too
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
