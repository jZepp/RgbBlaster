using RgbBlaster.Drivers.Lcd;
using P = RgbBlaster.Drivers.Lcd.LinkLcdProtocol;

namespace RgbBlaster.Tests;

public class LinkLcdTests
{
    private static byte[] Jpeg(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(P.MaxChunk, 1)]
    [InlineData(P.MaxChunk + 1, 2)]
    [InlineData(P.MaxChunk * 3, 3)]
    [InlineData(40_000, 40)]
    public void FrameSplitsIntoChunksAndFlagsOnlyTheLast(int length, int chunks)
    {
        var hw = new FakeHid();
        var jpeg = Jpeg(length);
        new LinkLcdDevice(hw, "").ShowJpeg(jpeg);

        Assert.Equal(chunks, hw.Sent.Count);
        var joined = new List<byte>();
        for (int i = 0; i < chunks; i++)
        {
            var r = hw.Sent[i];
            Assert.Equal(P.ReportLength, r.Length);
            Assert.Equal(new byte[] { 0x02, 0x05, 0x01 }, r[..3]);
            Assert.Equal(i == chunks - 1 ? 1 : 0, r[3]);
            Assert.Equal(i, r[4]);
            Assert.Equal(0, r[5]);
            int len = r[6] | r[7] << 8;
            Assert.Equal(Math.Min(P.MaxChunk, length - i * P.MaxChunk), len);
            Assert.All(r[(8 + len)..], b => Assert.Equal(0, b));
            joined.AddRange(r[8..(8 + len)]);
        }
        Assert.Equal(jpeg, joined.ToArray());
    }

    [Fact]
    public void OversizedFrameIsRejected() =>
        Assert.Throws<ArgumentException>(() => new LinkLcdDevice(new FakeHid(), "").ShowJpeg(new byte[P.MaxChunk * 255 + 1]));

    [Fact]
    public void FeatureReports()
    {
        var hw = new FakeHid();
        var lcd = new LinkLcdDevice(hw, "");
        lcd.SetBrightness(150);
        lcd.SetBrightness(0);
        lcd.SetRotation(5);
        lcd.Release();
        Assert.Equal(
            new[] { "03 0B 64 01", "03 0B 00 01", "03 19 00 01", "03 0C 01 01", "03 1E 01 01", "03 1D 00 01" },
            hw.Sent.Select(r => Convert.ToHexString(r).Insert(6, " ").Insert(4, " ").Insert(2, " ")));
    }

    [Fact]
    public void DashboardRendersA480SquareJpeg()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        using var dash = new LcdDashboard();
        foreach (var frame in new[] { dash.Render(61.4f, 48f, 2400), dash.Render(null, null, null), dash.Render(120, 10, 0) })
        {
            Assert.True(frame.Length < P.MaxChunk * 255);
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, frame[..2]);
            using var img = System.Drawing.Image.FromStream(new MemoryStream(frame));
            Assert.Equal((P.Width, P.Height), (img.Width, img.Height));
        }
    }

    [Fact]
    public void EveryBuiltInScreenRendersWithAndWithoutValues()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        using var dash = new LcdDashboard();
        foreach (var (id, _, _) in LcdDashboard.Screens)
            foreach (var v in new[] { new LcdValues(61.4f, 48f, 2400), new LcdValues(null, null, null), new LcdValues(140, -5, 9000) })
            {
                var frame = dash.Render(id, v);
                Assert.True(frame.Length < P.MaxChunk * 255, id);
                using var img = System.Drawing.Image.FromStream(new MemoryStream(frame));
                Assert.Equal((P.Width, P.Height), (img.Width, img.Height));
            }
    }
}
