using System.Buffers.Binary;
using System.Numerics;

namespace RgbBlaster.Core;

/// <summary>Sound levels at one moment. All values 0..1 after automatic gain.</summary>
/// <param name="Level">Overall loudness.</param>
/// <param name="Bands">Loudness per frequency band, lowest first, log-spaced from 40 Hz to 16 kHz.</param>
public sealed record AudioFrame(float Level, float[] Bands)
{
    public const int BandCount = 16;
    public static readonly AudioFrame Silence = new(0, new float[BandCount]);

    /// <summary>Average of the lowest three bands (about 40-130 Hz): kick drum and bass.</summary>
    public float Bass => Bands.Length >= 3 ? (Bands[0] + Bands[1] + Bands[2]) / 3 : Level;
}

/// <summary>Latest sound levels. Returns <see cref="AudioFrame.Silence"/> when nothing is received.</summary>
public interface IAudioSource
{
    AudioFrame Current { get; }
}

public sealed class SilentAudio : IAudioSource
{
    public static readonly SilentAudio Instance = new();
    public AudioFrame Current => AudioFrame.Silence;
}

public sealed class AudioConfig
{
    /// <summary>UDP port on 127.0.0.1 where the service receives sound levels from the tray app.</summary>
    public int Port { get; set; } = 27832;
}

/// <summary>
/// UDP datagram from the tray app to the service: "RBA1", sequence (uint32), level (float32), band count (byte), bands (float32 each).
/// Little-endian.
/// </summary>
public static class AudioPacket
{
    private static readonly uint Magic = BinaryPrimitives.ReadUInt32LittleEndian("RBA1"u8);

    public static byte[] Encode(uint sequence, AudioFrame f)
    {
        var b = new byte[13 + 4 * f.Bands.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), sequence);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), f.Level);
        b[12] = (byte)f.Bands.Length;
        for (int i = 0; i < f.Bands.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(13 + 4 * i), f.Bands[i]);
        return b;
    }

    /// <summary>Null for anything that is not a valid packet. Values are clamped to 0..1.</summary>
    public static AudioFrame? Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 13 || BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic) return null;
        int n = b[12];
        if (n > 64 || b.Length < 13 + 4 * n) return null;
        var bands = new float[n];
        for (int i = 0; i < n; i++) bands[i] = Clamp01(BinaryPrimitives.ReadSingleLittleEndian(b[(13 + 4 * i)..]));
        return new AudioFrame(Clamp01(BinaryPrimitives.ReadSingleLittleEndian(b[8..])), bands);
    }

    private static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
}

/// <summary>
/// Turns mono samples into <see cref="AudioFrame"/>s: Hann-windowed FFT, log-spaced bands, automatic gain and a fall-off,
/// so the lights follow the music at any volume setting. Not thread-safe.
/// </summary>
public sealed class AudioAnalyzer
{
    public const int FftSize = 2048;
    private const float MinHz = 40, MaxHz = 16000;
    /// <summary>Range shown, below the tracked peak.</summary>
    private const float RangeDb = 42;
    /// <summary>The peak never drops below this, so near-silence is not amplified to full brightness.</summary>
    private const float QuietestPeakDb = -48;
    /// <summary>Below this RMS the output is silence.</summary>
    private const float GateRms = 1e-4f;

    private readonly int _sampleRate;
    private readonly float[] _ring = new float[FftSize];
    private int _write;
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly (int Lo, int Hi)[] _bins = new (int, int)[AudioFrame.BandCount];
    private readonly float[] _shown = new float[AudioFrame.BandCount];
    private float _level, _peakDb = QuietestPeakDb, _levelPeakDb = QuietestPeakDb;

    public AudioAnalyzer(int sampleRate)
    {
        _sampleRate = sampleRate;
        for (int i = 0; i < FftSize; i++) _window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (FftSize - 1));
        float hzPerBin = sampleRate / (float)FftSize;
        for (int b = 0; b < AudioFrame.BandCount; b++)
        {
            float lo = MinHz * MathF.Pow(MaxHz / MinHz, b / (float)AudioFrame.BandCount);
            float hi = MinHz * MathF.Pow(MaxHz / MinHz, (b + 1) / (float)AudioFrame.BandCount);
            int l = Math.Max(1, (int)(lo / hzPerBin)), h = Math.Max(l + 1, (int)MathF.Ceiling(hi / hzPerBin));
            _bins[b] = (l, Math.Min(h, FftSize / 2));
        }
    }

    public void Add(ReadOnlySpan<float> mono)
    {
        foreach (var s in mono)
        {
            _ring[_write] = s;
            _write = (_write + 1) % FftSize;
        }
    }

    /// <summary>Analyzes the last <see cref="FftSize"/> samples. <paramref name="dt"/> is the time since the last call, for the fall-off.</summary>
    public AudioFrame Next(float dt)
    {
        double sumSq = 0;
        for (int i = 0; i < FftSize; i++)
        {
            float s = _ring[(_write + i) % FftSize];
            sumSq += s * s;
            _fft[i] = new Complex(s * _window[i], 0);
        }
        float rms = (float)Math.Sqrt(sumSq / FftSize);
        float fall = MathF.Exp(-dt * 7);   // shown values fall to a third in about 0.15 s
        if (rms < GateRms)
        {
            for (int b = 0; b < _shown.Length; b++) _shown[b] *= fall;
            _level *= fall;
            return new AudioFrame(_level, (float[])_shown.Clone());
        }

        Transform(_fft);
        // Full-scale sine through a Hann window gives a bin magnitude of N/4.
        const float fullScale = FftSize / 4f;
        var db = new float[AudioFrame.BandCount];
        float frameMax = float.MinValue;
        for (int b = 0; b < db.Length; b++)
        {
            var (lo, hi) = _bins[b];
            double power = 0;
            for (int k = lo; k < hi; k++) power = Math.Max(power, _fft[k].Magnitude / fullScale);
            // +3 dB per octave: music has less energy up high, and the treble bands would otherwise stay dark.
            float octaves = MathF.Log2((MinHz * MathF.Pow(MaxHz / MinHz, (b + 0.5f) / db.Length)) / MinHz);
            db[b] = 20 * MathF.Log10((float)power + 1e-9f) + 3 * octaves;
            frameMax = MathF.Max(frameMax, db[b]);
        }
        // Automatic gain: follow a louder peak at once, a quieter one at 6 dB per second.
        _peakDb = MathF.Max(QuietestPeakDb, MathF.Max(frameMax, _peakDb - 6 * dt));
        for (int b = 0; b < db.Length; b++)
        {
            float v = Math.Clamp((db[b] - (_peakDb - RangeDb)) / RangeDb, 0, 1);
            v *= v; // quiet bands stay low, peaks stand out
            _shown[b] = MathF.Max(v, _shown[b] * fall);
        }
        float levelDb = 20 * MathF.Log10(rms);
        _levelPeakDb = MathF.Max(QuietestPeakDb, MathF.Max(levelDb, _levelPeakDb - 6 * dt));
        float lv = Math.Clamp((levelDb - (_levelPeakDb - 30)) / 30, 0, 1);
        _level = MathF.Max(lv, _level * fall);
        return new AudioFrame(_level, (float[])_shown.Clone());
    }

    /// <summary>In-place radix-2 FFT.</summary>
    private static void Transform(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len)
            {
                var wk = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * wk;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    wk *= w;
                }
            }
        }
    }
}
