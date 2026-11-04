using RgbBlaster.Core;
using RgbBlaster.Drivers.Gpu;
using P = RgbBlaster.Drivers.Gpu.GigabyteGpuProtocol;

namespace RgbBlaster.Tests;

internal sealed class FakeI2c : II2cBus
{
    public readonly List<byte[]> Writes = [];
    public byte[] Reply = [0x01, 0x01, 0x01, 0x00];
    public void Write(byte address, ReadOnlySpan<byte> data) { Assert.Equal(P.Address, address); Writes.Add(data.ToArray()); }
    public void Read(byte address, Span<byte> data) => Reply.AsSpan(0, data.Length).CopyTo(data);
}

public class GpuTests
{
    [Fact]
    public void ProbeAcceptsNvidiaReplyAndRejectsOther()
    {
        var bus = new FakeI2c();
        Assert.True(GigabyteGpuDevice.Probe(bus));
        Assert.Equal([P.RegProbe, 0x01], bus.Writes.Single()[..2]);
        Assert.Equal(64, bus.Writes.Single().Length);
        Assert.False(GigabyteGpuDevice.Probe(new FakeI2c { Reply = [0, 0, 0, 0] }));
    }

    [Fact]
    public void GamingLayoutWritesSixHardwareZonesThenOnlyChanges()
    {
        var bus = new FakeI2c();
        var dev = new GigabyteGpuDevice(bus, "RTX 5070 Ti Gaming OC", GpuLayout.Gaming);
        Assert.Equal(4, dev.Zones.Count);
        dev.Open();
        dev.SetZone(3, [new Rgb(10, 20, 30)]);
        dev.Flush();
        Assert.Equal(6, bus.Writes.Count);
        var side = bus.Writes[3];
        Assert.Equal([P.RegColor, 0x01, P.ModeDirect, 0x00, P.BrightnessMax, 10, 20, 30, 0, 3, 0], side[..11]);
        Assert.Equal([10, 20, 30], bus.Writes[5][5..8]); // zones past the list take the last zone's color

        bus.Writes.Clear();
        dev.Flush();
        Assert.Empty(bus.Writes);
        dev.SetZone(0, [new Rgb(1, 2, 3)]);
        dev.Flush();
        Assert.Equal(0, bus.Writes.Single()[9]);
    }

    [Fact]
    public void SaveWritesStaticEffectsThenSave()
    {
        var bus = new FakeI2c();
        var dev = new GigabyteGpuDevice(bus, "RTX 5070 Ti Aero OC", GpuLayout.SingleZone);
        dev.SetZone(0, [new Rgb(255, 0, 0)]);
        dev.SaveLightingAndRelease();
        Assert.Equal(2, bus.Writes.Count);
        Assert.Equal([P.RegMode, 0x01, P.ModeStatic], bus.Writes[0][..3]);
        Assert.Equal([P.RegSave, 0x01], bus.Writes[1][..2]);
    }
}

public class NvApiLayoutTests
{
    [Fact]
    public void I2cInfoV3MatchesNativeSize()
    {
        var t = typeof(NvApi).GetNestedType("I2cInfoV3", System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(64, System.Runtime.InteropServices.Marshal.SizeOf(t)); // x64 layout of NV_I2C_INFO_V3
    }
}
