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

    [Fact]
    public void RandomEntryChangesEachTurnAndFixedEntriesStay()
    {
        var p = new Palette([Red, null]) { Seed = 3 };
        Assert.Equal(Red, p[0L]);
        Assert.Equal(Red, p[2L]);
        var turns = Enumerable.Range(0, 50).Select(i => p[(long)(i * 2 + 1)]).ToList();
        Assert.Equal(turns[4], p[9L]);                  // the same turn keeps its color while it moves
        Assert.True(turns.Distinct().Count() > 20);     // each turn of the random entry gets a new color
        Assert.Equal(p.Span(1), p.Span(1));             // gradients keep one color for it
    }

    [Fact]
    public void FactoryReadsRandomEntries()
    {
        var e = EffectFactory.Create(new EffectSpec { Type = "breathe", Colors = ["random"], Speed = 1 });
        var c = new Canvas(2, 2);
        var peaks = Enumerable.Range(0, 20).Select(i => { e.Render(c, i + 0.5); return c.Pixels[0]; }).ToList();
        Assert.True(peaks.Distinct().Count() > 5);      // each breath a new color
    }
}
