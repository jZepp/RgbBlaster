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
        var bg = new LcdBackground(Enumerable.Range(0, 8 * 8 * 3).Select(i => (byte)(i * 5)).ToArray(), 8, 8);
        foreach (var (id, _, _, _) in LcdDashboard.Screens)
            foreach (var v in new[] { new LcdValues(61.4f, 48f, 2400), new LcdValues(null, null, null), new LcdValues(140, -5, 9000) })
            {
                var frame = dash.Render(id, v, v.CpuC is null ? null : bg);
                Assert.True(frame.Length < P.MaxChunk * 255, id);
                using var img = System.Drawing.Image.FromStream(new MemoryStream(frame));
                Assert.Equal((P.Width, P.Height), (img.Width, img.Height));
            }
    }

    [Fact]
    public void LightingScreenShowsTheBackgroundColors()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        using var dash = new LcdDashboard();
        // Left half pure red, right half pure blue.
        var rgb = new byte[4 * 4 * 3];
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) { int o = (y * 4 + x) * 3; if (x < 2) rgb[o] = 255; else rgb[o + 2] = 255; }
        var frame = dash.Render("lighting", default, new LcdBackground(rgb, 4, 4));
        using var img = new System.Drawing.Bitmap(new MemoryStream(frame));
        var left = img.GetPixel(60, 240); var right = img.GetPixel(420, 240);
        Assert.True(left.R > 200 && left.B < 60, $"left {left}");
        Assert.True(right.B > 200 && right.R < 60, $"right {right}");
    }

    [Fact]
    public void EveryScreenRendersWithEveryBackgroundStyle()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        using var dash = new LcdDashboard();
        var bg = new LcdBackground(Enumerable.Repeat((byte)200, 8 * 8 * 3).ToArray(), 8, 8);
        LcdBackgroundStyle[] styles = [LcdBackgroundStyle.Off, new("flow"), new("bar"), new("bar", Vertical: true, BarWidth: 1), new("bar", BarWidth: 0.05f, PeriodSeconds: 0.3f)];
        foreach (var (id, _, _, _) in LcdDashboard.Screens)
            foreach (var style in styles)
            {
                var frame = dash.Render(id, new LcdValues(50, 40, 2000), bg, style);
                using var img = System.Drawing.Image.FromStream(new MemoryStream(frame));
                Assert.Equal((P.Width, P.Height), (img.Width, img.Height));
            }
    }

    [Fact]
    public void BarShowsABandOfTheBackgroundAndDarkElsewhere()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        using var dash = new LcdDashboard();
        var white = new LcdBackground(Enumerable.Repeat((byte)255, 4 * 4 * 3).ToArray(), 4, 4);
        // Sample across the middle row over a few frames: some pixels lit, some dark, whatever the bar position.
        bool sawLit = false, sawDark = false;
        for (int f = 0; f < 6 && !(sawLit && sawDark); f++)
        {
            var frame = dash.Render("lighting", default, white, new LcdBackgroundStyle("bar", PeriodSeconds: 1, BarWidth: 0.2f));
            using var img = new System.Drawing.Bitmap(new MemoryStream(frame));
            for (int x = 0; x < P.Width; x += 8)
            {
                var c = img.GetPixel(x, 240);
                if (c.R > 200) sawLit = true;
                if (c.R < 30) sawDark = true;
            }
            Thread.Sleep(150);
        }
        Assert.True(sawLit && sawDark, $"lit {sawLit}, dark {sawDark}");
    }
}
