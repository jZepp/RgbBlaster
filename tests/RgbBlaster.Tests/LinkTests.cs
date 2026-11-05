using System.Buffers.Binary;
using System.Text;
using RgbBlaster.Core;
using RgbBlaster.Drivers.Hid;
using RgbBlaster.Drivers.Link;
using L = RgbBlaster.Drivers.Link.LinkProtocol;

namespace RgbBlaster.Tests;

/// <summary>Simulates the hub: tracks the open endpoint and answers reads with that endpoint's data type.</summary>
internal sealed class FakeLinkHub : IHidTransport
{
    public readonly List<byte[]> Writes = [];
    public byte OpenEp;
    public Version Fw = new(2, 4, 0);
    public List<(byte Type, byte Model, string Serial)?> Channels = [];
    public short[] Rpm = [];
    public short[] Temp = [];
    public byte NextStatus;
    private byte[] _reply = new byte[513];

    public void Write(ReadOnlySpan<byte> r)
    {
        Writes.Add(r.ToArray());
        var cmd = r[3..];
        var reply = new byte[513]; // index 0 = report ID
        var body = reply.AsSpan(1);
        body[3] = NextStatus;
        NextStatus = 0;

        if (cmd.StartsWith(L.CmdFirmware)) { body[4] = (byte)Fw.Major; body[5] = (byte)Fw.Minor; }
        else if (cmd.StartsWith(L.CmdOpen) || cmd.StartsWith(L.CmdOpenColor)) OpenEp = cmd[2];
        else if (cmd.StartsWith(L.CmdRead)) FillRead(body);
        _reply = reply;
    }

    private void FillRead(Span<byte> b)
    {
        switch (OpenEp)
        {
            case L.EpDevices:
                L.TypeDevices.CopyTo(b[4..]);
                b[6] = (byte)Channels.Count;
                int pos = 7;
                foreach (var c in Channels)
                {
                    if (c is { } d)
                    {
                        b[pos + 2] = d.Type; b[pos + 3] = d.Model; b[pos + 7] = (byte)d.Serial.Length;
                        Encoding.ASCII.GetBytes(d.Serial).CopyTo(b[(pos + 8)..]);
                        pos += 8 + d.Serial.Length;
                    }
                    else pos += 8;
                }
                break;
            case L.EpSpeeds: Sensors(b, L.TypeSpeeds, Rpm); break;
            case L.EpTemperatures: Sensors(b, L.TypeTemperatures, Temp); break;
        }
    }

    private static void Sensors(Span<byte> b, ReadOnlySpan<byte> type, short[] v)
    {
        type.CopyTo(b[4..]);
        b[6] = (byte)v.Length;
        for (int i = 0; i < v.Length; i++)
        {
            b[7 + 3 * i] = v[i] < 0 ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt16LittleEndian(b[(8 + 3 * i)..], Math.Max(v[i], (short)0));
        }
    }

    public int Read(Span<byte> r, int timeoutMs) { _reply.CopyTo(r); return _reply.Length; }
    public void SetFeature(ReadOnlySpan<byte> r) { }
    public void GetFeature(Span<byte> r) { }
    public void Dispose() { }

    public IEnumerable<byte[]> Commands(ReadOnlySpan<byte> prefix)
    {
        var p = prefix.ToArray();
        return Writes.Where(w => w.AsSpan(3).StartsWith(p));
    }
}

public class LinkProtocolTests
{
    [Fact]
    public void CommandLayout()
    {
        var b = new byte[513];
        L.Command(b, L.CmdOpen, [L.EpSpeeds]);
        Assert.Equal([0x00, 0x00, 0x01, 0x0D, 0x01, 0x17, 0x00], b[..7]);
    }

    [Fact]
    public void FrameLayout()
    {
        var f = L.Frame(L.TypeSetSpeed, [1, 2, 3]);
        Assert.Equal([0x05, 0x00, 0x00, 0x00, 0x07, 0x00, 1, 2, 3], f);
    }

    [Fact]
    public void SpeedPayloadClampsAndPacks()
    {
        var p = L.SpeedPayload([(1, 40), (7, 150)]);
        Assert.Equal([2, 1, 0, 40, 0, 7, 0, 100, 0], p);
    }

    [Fact]
    public void ParseDevicesSkipsEmptyChannels()
    {
        var r = new byte[100];
        r[6] = 3;
        int pos = 7;
        r[pos + 2] = 0x0F; r[pos + 7] = 4; "ABCD"u8.CopyTo(r.AsSpan(pos + 8)); pos += 12;
        pos += 8; // channel 2 empty
        r[pos + 2] = 0x11; r[pos + 3] = 0x02; r[pos + 7] = 2; "XY"u8.CopyTo(r.AsSpan(pos + 8));
        var d = L.ParseDevices(r, []);
        Assert.Equal(2, d.Count);
        Assert.Equal((byte)1, d[0].Channel);
        Assert.Equal("RX RGB fan", d[0].Info!.Name);
        Assert.Equal((byte)3, d[1].Channel);
        Assert.Equal("TITAN 360", d[1].Info!.Name);
        Assert.Equal("XY", d[1].Serial);
    }

    [Fact]
    public void ParseSensorsUnavailableIsNull()
    {
        var r = new byte[20];
        r[6] = 2;
        r[7] = 0; BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(8), 1234);
        r[10] = 1;
        var v = L.ParseSensors(r);
        Assert.Equal((short)1234, v[0]);
        Assert.Null(v[1]);
    }

    [Theory]
    [InlineData(2, 4, false)] [InlineData(2, 5, true)] [InlineData(3, 0, true)]
    public void ContinuationByFirmware(int major, int minor, bool expected) =>
        Assert.Equal(expected, L.DeviceListHasContinuation(new Version(major, minor)));
}

public class LinkHubDeviceTests
{
    private static FakeLinkHub Hub() => new()
    {
        Channels = [(0x11, 0x02, "AIO"), (0x0F, 0x00, "F1"), null, (0x0F, 0x00, "F2")],
        Rpm = [0, 2800, 1200, -1, 1100],
        Temp = [-1, 312],
    };

    [Fact]
    public void OpenEntersSoftwareModeAndBuildsZonesAndFans()
    {
        var hw = Hub();
        var dev = new LinkHubDevice(hw);
        dev.Open();
        Assert.Single(hw.Commands(L.CmdSoftwareMode));
        Assert.Equal(["ch1", "ch2", "ch4"], dev.Zones.Select(z => z.Id));
        Assert.Equal([20, 8, 8], dev.Zones.Select(z => z.LedCount));
        Assert.Equal(3, dev.Fans.Count);
        Assert.Equal(2800, dev.Fans[0].ReadRpm());
        Assert.Equal(1100, dev.Fans[2].ReadRpm());
        Assert.Equal(31.2f, dev.Temperatures[1]!.Value, 3);
    }

    [Fact]
    public void OpenWritesSafeDefaultDuty()
    {
        var hw = Hub();
        new LinkHubDevice(hw).Open();
        var w = Assert.Single(hw.Commands(L.CmdWrite));
        // [3..4]=06 01, frame starts at 5: len(2) 00 00 type(2) payload
        var payload = w.AsSpan(5 + 6);
        Assert.Equal(3, payload[0]);
        Assert.Equal([1, 0, 100, 0], payload[1..5].ToArray()); // AIO pump channel
        Assert.Equal([2, 0, 50, 0], payload[5..9].ToArray());
    }

    [Fact]
    public void PumpDutyIsClampedToMinimum()
    {
        var hw = Hub();
        var dev = new LinkHubDevice(hw, pumpMinPercent: 60);
        dev.Open();
        hw.Writes.Clear();
        dev.Fans[0].SetDutyPercent(10);
        dev.Fans[1].SetDutyPercent(10);
        dev.Poll();
        var payload = Assert.Single(hw.Commands(L.CmdWrite)).AsSpan(11);
        Assert.Equal(60, payload[3]);
        Assert.Equal(10, payload[7]);
    }

    [Fact]
    public void FlushSendsColorFrameOnceUntilChanged()
    {
        var hw = Hub();
        var dev = new LinkHubDevice(hw);
        dev.Open();
        hw.Writes.Clear();

        dev.SetZone(1, Enumerable.Repeat(new Rgb(1, 2, 3), 8).ToArray());
        dev.Flush();
        var w = Assert.Single(hw.Commands(L.CmdWriteColor));
        Assert.Equal(0x22, hw.Commands(L.CmdOpenColor).Single()[5]);
        int len = BinaryPrimitives.ReadUInt16LittleEndian(w.AsSpan(5));
        Assert.Equal(36 * 3 + 2, len);
        Assert.Equal([0x12, 0x00], w[9..11]);
        Assert.Equal([1, 2, 3], w[(11 + 20 * 3)..(11 + 20 * 3 + 3)]); // ch2 follows the AIO's 20 LEDs

        hw.Writes.Clear();
        dev.Flush();
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void FlushResendsUnchangedFrameAfterKeepAlive()
    {
        var hw = Hub();
        var dev = new LinkHubDevice(hw) { KeepAlive = TimeSpan.Zero, MinFrameInterval = TimeSpan.Zero };
        dev.Open();
        dev.Flush();
        hw.Writes.Clear();
        dev.Flush();
        Assert.Single(hw.Commands(L.CmdWriteColor));
    }

    [Fact]
    public void ColorEndpointOpensOnceAcrossFrames()
    {
        var hw = Hub();
        var dev = new LinkHubDevice(hw) { MinFrameInterval = TimeSpan.Zero };
        dev.Open();
        hw.Writes.Clear();
        for (int i = 0; i < 3; i++)
        {
            dev.SetZone(0, [new Rgb((byte)(i + 1), 0, 0)]);
            dev.Flush();
        }
        Assert.Equal(3, hw.Commands(L.CmdWriteColor).Count());
        Assert.Single(hw.Commands(L.CmdOpenColor));
    }

    [Fact]
    public void ChangedFrameWaitsForMinimumInterval()
    {
        var hw = Hub();
        var dev = new LinkHubDevice(hw) { MinFrameInterval = TimeSpan.FromHours(1) };
        dev.Open();
        dev.Flush();
        hw.Writes.Clear();
        dev.SetZone(0, [new Rgb(9, 9, 9)]);
        dev.Flush();
        Assert.Empty(hw.Commands(L.CmdWriteColor));
    }

    [Fact]
    public void LargeColorFrameUsesContinuationPackets()
    {
        var hw2 = new FakeLinkHub { Channels = [(0x05, 0x02, "A"), (0x01, 0x00, "B")] }; // 160 + 34 LEDs = 586 bytes
        var dev2 = new LinkHubDevice(hw2);
        dev2.Open();
        hw2.Writes.Clear();
        dev2.SetZone(0, new Rgb[160].Select(_ => Rgb.White).ToArray());
        dev2.Flush();
        Assert.Single(hw2.Commands(L.CmdWriteColor));
        Assert.Single(hw2.Commands(L.CmdWriteColorNext));
    }

    [Fact]
    public void WrongModeStatusReentersSoftwareMode()
    {
        var hw = Hub();
        var dev = new LinkHubDevice(hw);
        dev.Open();
        hw.Writes.Clear();
        hw.NextStatus = L.StatusWrongMode;
        dev.Poll();
        dev.Poll();
        Assert.Single(hw.Commands(L.CmdSoftwareMode));
    }

    [Fact]
    public void ReleaseSendsHardwareMode()
    {
        var hw = Hub();
        var dev = new LinkHubDevice(hw);
        dev.Open();
        dev.Release();
        Assert.Single(hw.Commands(L.CmdHardwareMode));
    }
}
