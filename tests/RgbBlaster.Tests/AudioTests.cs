using RgbBlaster.Core;
using RgbBlaster.Engine;
using Xunit;

namespace RgbBlaster.Tests;

public class AudioTests
{
    private const int Rate = 48000;

    private static AudioFrame Analyze(Func<int, float> sample, int seconds = 1)
    {
        var a = new AudioAnalyzer(Rate);
        AudioFrame f = AudioFrame.Silence;
        var chunk = new float[Rate / 60];
        int n = 0;
        for (int frame = 0; frame < 60 * seconds; frame++)
        {
            for (int i = 0; i < chunk.Length; i++) chunk[i] = sample(n++);
            a.Add(chunk);
            f = a.Next(1 / 60f);
        }
        return f;
    }

    private static Func<int, float> Sine(float hz, float amp) => i => amp * MathF.Sin(2 * MathF.PI * hz * i / Rate);

    [Fact]
    public void BassToneLightsTheLowBands()
    {
        var f = Analyze(Sine(60, 0.5f));
        int top = Array.IndexOf(f.Bands, f.Bands.Max());
        Assert.InRange(top, 0, 2);
        Assert.True(f.Bands[^1] < 0.2f);
        Assert.True(f.Level > 0.5f);
    }

    [Fact]
    public void TrebleToneLightsTheHighBands()
    {
        var f = Analyze(Sine(8000, 0.5f));
        int top = Array.IndexOf(f.Bands, f.Bands.Max());
        Assert.InRange(top, 12, 15);
        Assert.True(f.Bands[0] < 0.2f);
    }

    [Fact]
    public void QuietMusicStillReachesFullScaleThroughAutoGain()
    {
        var f = Analyze(Sine(200, 0.02f), seconds: 2);
        Assert.True(f.Bands.Max() > 0.8f);
    }

    [Fact]
    public void SilenceFallsToZero()
    {
        var a = new AudioAnalyzer(Rate);
        a.Add(new float[AudioAnalyzer.FftSize].Select((_, i) => Sine(100, 0.5f)(i)).ToArray());
        a.Next(1 / 60f);
        a.Add(new float[AudioAnalyzer.FftSize]);
        AudioFrame f = AudioFrame.Silence;
        for (int i = 0; i < 60; i++) f = a.Next(1 / 60f);
        Assert.True(f.Level < 0.01f && f.Bands.Max() < 0.01f);
    }

    [Fact]
    public void PacketRoundTripsAndRejectsOtherData()
    {
        var f = new AudioFrame(0.5f, Enumerable.Range(0, 16).Select(i => i / 16f).ToArray());
        var back = AudioPacket.Decode(AudioPacket.Encode(7, f));
        Assert.NotNull(back);
        Assert.Equal(0.5f, back.Level);
        Assert.Equal(f.Bands, back.Bands);
        Assert.Null(AudioPacket.Decode("hello world, not audio"u8));
        Assert.Null(AudioPacket.Decode([]));
    }

    [Fact]
    public void BeatDetectorFindsKicksAt120Bpm()
    {
        var d = new BeatDetector();
        int beats = 0;
        for (int frame = 0; frame < 60 * 4; frame++)
        {
            double t = frame / 60.0;
            float bass = 0.1f + 0.8f * MathF.Exp(-(float)(t % 0.5) * 12);
            if (d.Update(bass, 1 / 60f, t)) beats++;
        }
        Assert.InRange(beats, 7, 8); // 8 kicks in 4 s; the first may be missed while the average settles
    }

    private sealed class FixedAudio(AudioFrame f) : IAudioSource { public AudioFrame Current => f; }

    [Fact]
    public void SpectrumBarsFollowTheBands()
    {
        var bands = new float[16];
        bands[0] = 1;                                   // full bass, nothing else
        var e = EffectFactory.Create(new EffectSpec { Type = "spectrum", Colors = ["#00FF00", "#FF0000"] }, new FixedAudio(new AudioFrame(1, bands)));
        var c = new Canvas(32, 20);
        e.Render(c, 0);
        Assert.NotEqual(Rgb.Black, c.Row(19)[0]);       // front, bottom: lit
        Assert.NotEqual(Rgb.Black, c.Row(1)[0]);        // front, top: lit (bar at full height)
        Assert.Equal(Rgb.Black, c.Row(19)[31]);         // rear: treble silent
    }

    [Fact]
    public void FollowSoundDarkensInSilenceAndSoundEffectsAreFlagged()
    {
        var spec = new EffectSpec { Type = "solid", Colors = ["#FFFFFF"], Sound = "brightness", SoundAmount = 1 };
        Assert.True(spec.UsesSound);
        Assert.True(new EffectSpec { Type = "beat" }.UsesSound);
        Assert.False(new EffectSpec { Type = "wave" }.UsesSound);
        var c = new Canvas(4, 4);
        EffectFactory.Create(spec, SilentAudio.Instance).Render(c, 1);
        Assert.Equal(Rgb.Black, c.Pixels[0]);
        EffectFactory.Create(spec, new FixedAudio(new AudioFrame(1, new float[16]))).Render(c, 1);
        Assert.Equal(Rgb.White, c.Pixels[0]);
    }

    [Fact]
    public void BeatPulseDrawsARingAfterAKick()
    {
        var src = new MutableAudio();
        var e = EffectFactory.Create(new EffectSpec { Type = "beat", Colors = ["#FF0000"], Speed = 0.25f }, src);
        var c = new Canvas(40, 40);
        for (int i = 0; i < 60; i++) e.Render(c, i / 60.0);           // quiet second: average settles
        var kick = new float[16]; kick[0] = kick[1] = kick[2] = 1;
        src.Frame = new AudioFrame(0, kick);
        e.Render(c, 1.0);
        src.Frame = AudioFrame.Silence;
        e.Render(c, 1.2);                                              // ring radius 0.2 of the canvas
        Assert.Contains(c.Pixels, p => p.R > 100);
    }

    [Fact]
    public void FrequencyRingsLightTheCenterOnBassAndTheEdgeOnTreble()
    {
        var spec = new EffectSpec { Type = "radial", Colors = ["#FFFFFF"], Sound = "rings", SoundAmount = 1, CenterX = 0.5f, CenterY = 0.5f };
        Assert.True(spec.UsesSound);
        Assert.False(new EffectSpec { Type = "wave", Sound = "rings" }.UsesSound);
        var bass = new float[16]; bass[0] = bass[1] = 1;
        var treble = new float[16]; treble[14] = treble[15] = 1;
        var c = new Canvas(40, 40);
        EffectFactory.Create(spec, new FixedAudio(new AudioFrame(1, bass))).Render(c, 0);
        Assert.Equal(Rgb.White, c.Row(20)[20]);       // center lit
        Assert.Equal(Rgb.Black, c.Row(0)[0]);         // corner dark
        EffectFactory.Create(spec, new FixedAudio(new AudioFrame(1, treble))).Render(c, 0);
        Assert.Equal(Rgb.Black, c.Row(20)[20]);
        Assert.NotEqual(Rgb.Black, c.Row(0)[0]);
    }

    private sealed class MutableAudio : IAudioSource
    {
        public AudioFrame Frame = AudioFrame.Silence;
        public AudioFrame Current => Frame;
    }
}
