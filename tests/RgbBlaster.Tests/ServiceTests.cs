using RgbBlaster.Core;
using RgbBlaster.Drivers.Lcd;
using RgbBlaster.Engine;
using RgbBlaster.Service;

namespace RgbBlaster.Tests;

public class EffectFactoryTests
{
    [Theory]
    [InlineData("solid", "solid")]
    [InlineData("breathe", "breathe")]
    [InlineData("wave", "wave")]
    [InlineData("radial", "radial")]
    [InlineData("cycle", "cycle")]
    [InlineData("gradient", "gradient")]
    [InlineData("matrix", "matrix")]
    [InlineData("fire", "fire")]
    [InlineData("no-such-effect", "rainbow")]
    public void CreatesEachType(string type, string name) =>
        Assert.Equal(name, EffectFactory.Create(new EffectSpec { Type = type }).Name);

    [Fact]
    public void PaletteLoopsThroughEveryColorAndBackToTheFirst()
    {
        var p = Palette.Of(new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255));
        Assert.Equal(new Rgb(255, 0, 0), p.Loop(0));
        Assert.Equal(new Rgb(0, 255, 0), p.Loop(1f / 3));
        Assert.Equal(new Rgb(0, 0, 255), p.Loop(2f / 3));
        Assert.Equal(p.Loop(0.1f), p.Loop(1.1f));
        Assert.Equal(new Rgb(0, 0, 255), p.Span(1));
    }

    [Fact]
    public void WaveUsesEveryPaletteColor()
    {
        var spec = new EffectSpec { Type = "wave", Colors = ["#FF0000", "#00FF00", "#0000FF", "#FFFFFF"], Speed = 0.01f };
        var c = new Canvas(400, 4);
        EffectFactory.Create(spec).Render(c, 0);
        foreach (var want in new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255), new Rgb(255, 255, 255) })
            Assert.Contains(want, c.Pixels);
    }

    [Fact]
    public void GradientRunsFirstToLastColorEdgeToEdge()
    {
        var c = new Canvas(100, 2);
        new GradientEffect(Palette.Of(Rgb.Black, Rgb.White), 0).Render(c, 0);
        Assert.True(c.Pixels[0].R < 5);
        Assert.True(c.Pixels[99].R > 250);
    }

    [Fact]
    public void MatrixDropsFallAndUsePaletteColors()
    {
        var green = new Rgb(0, 255, 0);
        var e = new MatrixEffect(Palette.Of(green), 1f, 1f);
        var c = new Canvas(80, 80);
        for (int i = 0; i <= 60; i++) e.Render(c, i / 60.0);
        Assert.Contains(c.Pixels, p => p.G > 0);
        Assert.DoesNotContain(c.Pixels, p => p.R > p.G);            // tails stay green; only heads blend toward white
        Assert.Equal(NativeEffectKind.Sparkle, e.Native.Kind);
    }

    [Fact]
    public void SpacingLeavesDarkGapsBetweenBands()
    {
        var p = Palette.Of(new Rgb(0, 0, 255));
        Assert.Equal(new Rgb(0, 0, 255), p.Bands(0.25f, 0.5f));   // middle of the lit half
        Assert.Equal(Rgb.Black, p.Bands(0.75f, 0.5f));            // dark half
        Assert.Equal(p.Loop(0.3f), p.Bands(0.3f, 0));             // no spacing: same as before
    }

    [Fact]
    public void FireIsHotAtTheBottomAndDarkAtTheTop()
    {
        var c = new Canvas(160, 100);
        var e = EffectFactory.Create(new EffectSpec { Type = "fire", Colors = ["#800000", "#FF8000", "#FFFF80"], Height = 0.5f });
        for (int i = 0; i <= 30; i++) e.Render(c, i / 30.0);
        static int Sum(Rgb p) => p.R + p.G + p.B;
        long bottom = 0, top = 0;
        for (int x = 0; x < c.Width; x++) { bottom += Sum(c.Pixels[(c.Height - 2) * c.Width + x]); top += Sum(c.Pixels[x]); }
        Assert.True(bottom > top * 4 && bottom > 0, $"bottom {bottom}, top {top}");
        Assert.DoesNotContain(c.Pixels, p => p.B > p.R);           // stays within the warm palette
    }

    [Fact]
    public void RingRotationMovesLedOneAndReverseRunsCounterClockwise()
    {
        const int w = 100, h = 100;
        (int X, int Y) At(int i) => (i % w, i / w);
        var ring = new ZonePlacement { Shape = PlacementShape.Ring, X = 0.5f, Y = 0.5f, Radius = 0.4f };
        var plain = LayoutMapper.Map(ring, 4, w, h);
        Assert.True(At(plain[0]).Y < 20);                     // LED 1 at the top
        Assert.True(At(plain[1]).X > 80);                     // then clockwise: right
        ring.Rotation = 90;
        Assert.True(At(LayoutMapper.Map(ring, 4, w, h)[0]).X > 80);   // LED 1 now at 3 o'clock
        ring.Rotation = 0; ring.Reverse = true;
        Assert.True(At(LayoutMapper.Map(ring, 4, w, h)[1]).X < 20);   // counter-clockwise: left
    }

    [Fact]
    public void LineReverseStartsAtTheOtherEnd()
    {
        var line = new ZonePlacement { Shape = PlacementShape.Line, X = 0, Y = 0.5f, X2 = 1, Y2 = 0.5f, Reverse = true };
        var idx = LayoutMapper.Map(line, 5, 100, 10);
        Assert.True(idx[0] % 100 > idx[4] % 100);
    }

    [Fact]
    public void GridRotationTurnsAboutTheCentre()
    {
        var grid = new ZonePlacement { Shape = PlacementShape.Grid, X = 0.25f, Y = 0.25f, X2 = 0.75f, Y2 = 0.75f, Rotation = 180 };
        var idx = LayoutMapper.Map(grid, 2, 100, 100, [(0f, 0f), (1f, 1f)]);
        Assert.True(idx[0] % 100 >= 70 && idx[0] / 100 >= 70);   // top-left corner turned to bottom-right
        Assert.True(idx[1] % 100 <= 30 && idx[1] / 100 <= 30);
    }

    [Fact]
    public void OldSettingsWithoutPaletteUseColorAndColor2()
    {
        var spec = new EffectSpec { Type = "solid", Color = "#102030" };
        var c = new Canvas(1, 1);
        EffectFactory.Create(spec).Render(c, 0);
        Assert.Equal(new Rgb(0x10, 0x20, 0x30), c.Pixels[0]);
    }

    [Fact]
    public void BadColorFallsBackInsteadOfThrowing() =>
        EffectFactory.Create(new EffectSpec { Type = "solid", Color = "nope" }).Render(new Canvas(4, 4), 0);

    [Fact]
    public void BrightnessScalesOutputAndKeepsNativeEffect()
    {
        var e = EffectFactory.Create(new EffectSpec { Type = "solid", Color = "#C86400", Brightness = 0.5f });
        var c = new Canvas(2, 2);
        e.Render(c, 0);
        Assert.Equal(new Rgb(100, 50, 0), c.Pixels[0]);
        var rainbow = EffectFactory.Create(new EffectSpec { Type = "rainbow", Brightness = 0.5f });
        Assert.Equal(NativeEffectKind.Rainbow, rainbow.Native.Kind);
    }

    [Fact]
    public void WaveRunsAlongItsAngle()
    {
        // Angle 0 travels along x: every column is uniform top to bottom, and colors change across x.
        var c = new Canvas(40, 10);
        new WaveEffect(Rgb.Black, Rgb.White, 0, 0, 1).Render(c, 0);
        for (int x = 0; x < c.Width; x++)
            for (int y = 1; y < c.Height; y++)
                Assert.Equal(c.Pixels[x], c.Pixels[y * c.Width + x]);
        Assert.NotEqual(c.Pixels[0], c.Pixels[c.Width / 2]);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void FirstLoadSeedsDefaultProfileAndSaveRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rgbblaster-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var seed = new AppConfig
            {
                Layout = [new ZonePlacement { Device = "link", Zone = "ch1", Shape = PlacementShape.Ring, X = 0.5f }],
                FanCurves = [new FanCurveConfig { Name = "case", Points = [new() { Temp = 40, Duty = 30 }] }],
            };
            var store = new SettingsStore(dir);
            var s = store.Load(seed);
            Assert.Equal("Default", s.ActiveProfile);
            Assert.Single(s.Profiles);
            Assert.Equal("case", s.Profiles[0].FanCurves[0].Name);
            Assert.Equal("ch1", s.Layout[0].Zone);
            Assert.True(Directory.Exists(store.MediaDirectory));

            s.Profiles[0].Effect.Type = "wave";
            s.Profiles.Add(new Profile { Name = "Quiet" });
            s.ActiveProfile = "Quiet";
            store.Save(s);

            var again = store.Load(new AppConfig());
            Assert.Equal("Quiet", again.ActiveProfile);
            Assert.Equal("wave", again.Profiles[0].Effect.Type);
            Assert.Equal(PlacementShape.Ring, again.Layout[0].Shape);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class LcdMediaTests
{
    [Fact]
    public void AnimatedGifBecomesTimedFrames()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        using var fs = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Data", "three-frames.gif"));
        var frames = LcdMedia.Decode(fs);
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(300, f.DelayMs));
        Assert.All(frames, f => Assert.Equal(new byte[] { 0xFF, 0xD8 }, f.Jpeg[..2]));
        Assert.NotEqual(frames[0].Jpeg, frames[1].Jpeg);
    }

    [Fact]
    public void NonImageIsRejected()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        Assert.ThrowsAny<Exception>(() => LcdMedia.Decode(new MemoryStream("not an image"u8.ToArray())));
    }
}
