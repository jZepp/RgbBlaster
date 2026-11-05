using RgbBlaster.Core;
using RgbBlaster.Engine;
using Xunit;

namespace RgbBlaster.Tests;

public class PaletteVariationTests
{
    private static readonly Rgb Red = new(255, 0, 0), Blue = new(0, 0, 255);

    [Fact]
    public void WithoutVariationSlotsRepeatThePalette()
    {
        var p = Palette.Of(Red, Blue);
        Assert.Equal(Red, p[0L]);
        Assert.Equal(Blue, p[1L]);
        Assert.Equal(Red, p[4L]);
        Assert.Equal(Blue, p[-1L]);
    }

    [Fact]
    public void RandomBandsKeepTheirColorAndStayWithinTheAmount()
    {
        var p = new Palette([Red]) { RandomBands = true, Variation = 30, Seed = 7 };
        var colors = Enumerable.Range(0, 200).Select(i => p[(long)i]).ToList();
        Assert.Equal(colors[5], p[5L]);                 // same band, same color
        Assert.True(colors.Distinct().Count() > 20);    // bands differ
        foreach (var c in colors)
        {
            // Red turned by at most 30 degrees: blue stays 0, or green stays 0, and the other channel is at most half of red.
            Assert.Equal(255, c.R);
            Assert.True(c.G <= 128 && c.B <= 128 && (c.G == 0 || c.B == 0), c.ToString());
        }
    }

    [Fact]
    public void FullAmountVariesWhiteToo()
    {
        var p = new Palette([Rgb.White]) { RandomBands = true, Variation = 180, Seed = 3 };
        Assert.Contains(Enumerable.Range(0, 50).Select(i => p[(long)i]), c => c != Rgb.White);
    }

    [Fact]
    public void DriftTurnsEveryColor()
    {
        var p = Palette.Of(Red);
        p.SetDrift(120);
        Assert.Equal(new Rgb(0, 255, 0), p[0L]);
        Assert.Equal(new Rgb(0, 255, 0), p.Span(0.5f));
        p.SetDrift(0);
        Assert.Equal(Red, p[0L]);
    }

    [Fact]
    public void FactoryWiresVariationForEveryColorEffect()
    {
        foreach (var type in new[] { "solid", "breathe", "wave", "radial", "cycle", "gradient", "matrix", "fire" })
        {
            var drift = EffectFactory.Create(new EffectSpec { Type = type, Colors = ["#FF0000"], Variation = "drift", VariationAmount = 90 });
            Assert.IsType<ColorDriftEffect>(drift);
            var canvas = new Canvas(16, 8);
            drift.Render(canvas, 5);   // must not throw
            EffectFactory.Create(new EffectSpec { Type = type, Colors = ["#FF0000", "#0000FF"], Variation = "random", VariationAmount = 45 }).Render(canvas, 5);
        }
    }
}
