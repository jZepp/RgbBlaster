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
