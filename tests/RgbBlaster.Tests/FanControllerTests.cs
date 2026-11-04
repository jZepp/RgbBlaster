using RgbBlaster.Core;
using RgbBlaster.Engine;

namespace RgbBlaster.Tests;

internal sealed class FakeFan(string id) : IFanChannel
{
    public readonly List<byte> Sets = [];
    public string Id => id;
    public string Name => id;
    public bool IsPump => false;
    public int? ReadRpm() => null;
    public void SetDutyPercent(byte percent) => Sets.Add(percent);
}

internal sealed class FakeTemps : ITemperatureSource
{
    public readonly Dictionary<string, float?> Values = [];
    public float? ReadCelsius(string sensor) => Values.GetValueOrDefault(sensor);
}

public class FanControllerTests
{
    private static FanCurveConfig Curve(params string[] fans) => new()
    {
        Name = "c",
        Sensors = ["cpu", "gpu"],
        Points = [new() { Temp = 40, Duty = 30 }, new() { Temp = 80, Duty = 100 }],
        Fans = [.. fans],
        RisePercentPerSecond = 10,
        FallPercentPerSecond = 2,
    };

    [Fact]
    public void InterpolatesAndHoldsFlatBeyondEnds()
    {
        var p = Curve().Points;
        Assert.Equal(30, FanController.Interpolate(p, 20));
        Assert.Equal(65, FanController.Interpolate(p, 60));
        Assert.Equal(100, FanController.Interpolate(p, 95));
    }

    [Fact]
    public void UsesHottestSensorAndLimitsRiseAndFall()
    {
        var fan = new FakeFan("board1");
        var temps = new FakeTemps { Values = { ["cpu"] = 40, ["gpu"] = 60 } };
        var ctl = new FanController([Curve("board1")], [fan], temps);
        ctl.Tick(1);
        Assert.Equal(65, fan.Sets.Last());              // first tick jumps to target (gpu 60 C)

        temps.Values["gpu"] = 80;
        ctl.Tick(1);
        Assert.Equal(75, fan.Sets.Last());              // rise limited to 10 %/s

        temps.Values["gpu"] = 20;
        temps.Values["cpu"] = 20;
        ctl.Tick(1);
        Assert.Equal(73, fan.Sets.Last());              // fall limited to 2 %/s
    }

    [Fact]
    public void NoReadingGoesToFailsafeAndUnchangedDutyIsNotResent()
    {
        var fan = new FakeFan("board1");
        var temps = new FakeTemps();
        var ctl = new FanController([Curve("board1")], [fan], temps);
        ctl.Tick(1);
        ctl.Tick(1);
        Assert.Equal([100], fan.Sets);
    }

    [Fact]
    public void UnknownAndDuplicateFansAreReported()
    {
        var a = new FakeFan("a");
        var ctl = new FanController([Curve("a", "missing"), Curve("a")], [a], new FakeTemps());
        Assert.Contains(ctl.Warnings, w => w.Contains("missing"));
        Assert.Contains(ctl.Warnings, w => w.Contains("already"));
    }
}
