using System.Diagnostics;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RgbBlaster.Core;

namespace RgbBlaster.Tray;

/// <summary>
/// Captures what the default playback device plays (WASAPI loopback) and sends <see cref="AudioFrame"/>s to the service
/// over UDP on 127.0.0.1, 60 times per second. The service cannot capture this itself: it runs in session 0.
/// Follows changes of the default device.
/// </summary>
internal sealed class SoundCapture : IDisposable
{
    private const double SendIntervalMs = 1000 / 60.0;

    private readonly object _gate = new();
    private WasapiRecorder? _capture;
    private AudioAnalyzer? _analyzer;
    private WaveFormat? _format;
    private string? _deviceId;
    private UdpClient? _udp;
    private Thread? _thread;
    private volatile bool _running, _restart;
    private long _lastDataTicks;
    private float[] _mono = [];

    public bool Running => _running;
    public string? Error { get; private set; }

    public void Start(int port)
    {
        if (_running) return;
        _udp = new UdpClient();
        _udp.Connect("127.0.0.1", port);
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "SoundCapture" };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(1000);
        StopCapture();
        _udp?.Dispose();
        _udp = null;
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double last = 0, lastDeviceCheck = -10, next = 0;
        uint seq = 0;
        using var timer = new FrameTimer();
        while (_running)
        {
            double now = clock.Elapsed.TotalSeconds;
            // Default device: checked every 3 s; capture restarts when it changes or after an error.
            if (_restart || _capture is null || now - lastDeviceCheck > 3)
            {
                lastDeviceCheck = now;
                try
                {
                    string id = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
                    if (_restart || _capture is null || id != _deviceId) StartCapture();
                    Error = null;
                }
                catch (Exception ex) { Error = ex.Message; StopCapture(); }
            }

            AudioFrame frame;
            lock (_gate)
            {
                float dt = (float)(now - last);
                // Loopback delivers no data while nothing plays; feed silence so the levels fall to zero.
                if (_analyzer is not null && _format is not null && Stopwatch.GetElapsedTime(_lastDataTicks).TotalMilliseconds > 100)
                    _analyzer.Add(new float[Math.Min(AudioAnalyzer.FftSize, (int)(_format.SampleRate * dt))]);
                frame = _analyzer?.Next(dt) ?? AudioFrame.Silence;
            }
            last = now;
            try { _udp?.Send(AudioPacket.Encode(seq++, frame)); }
            catch (SocketException) { /* service not listening; keep trying */ }
            // Thread.Sleep follows the 15.6 ms system timer and would halve the rate.
            next = Math.Max(next + SendIntervalMs / 1000.0, clock.Elapsed.TotalSeconds);
            timer.Wait(TimeSpan.FromSeconds(next - clock.Elapsed.TotalSeconds));
        }
    }

    private void StartCapture()
    {
        StopCapture();
        var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var capture = new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().WithSharedMode().Build();
        lock (_gate)
        {
            _format = capture.WaveFormat;
            _analyzer = new AudioAnalyzer(_format.SampleRate);
            _deviceId = device.ID;
            _restart = false;
        }
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, e) => { if (e.Exception is not null) { Error = e.Exception.Message; _restart = true; } };
        capture.StartRecording();
        _capture = capture;
    }

    private void StopCapture()
    {
        var c = _capture;
        _capture = null;
        if (c is null) return;
        c.DataAvailable -= OnData;
        try { c.StopRecording(); } catch { /* already stopped */ }
        c.Dispose();
    }

    private void OnData(ReadOnlySpan<byte> src, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var f = _format;
        if (f is null || src.Length == 0) return;
        int bytesPerSample = f.BitsPerSample / 8, ch = Math.Max(1, f.Channels);
        int frames = src.Length / (bytesPerSample * ch);
        if (_mono.Length < frames) _mono = new float[frames];
        bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
        // Shared-mode loopback is normally 32-bit float; 16-bit PCM is handled for odd drivers.
        bool isFloat = f.BitsPerSample == 32 && f.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible;
        for (int i = 0; i < frames && !silent; i++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++)
            {
                int o = (i * ch + c) * bytesPerSample;
                sum += isFloat ? BitConverter.ToSingle(src[o..]) : bytesPerSample == 2 ? BitConverter.ToInt16(src[o..]) / 32768f : 0;
            }
            _mono[i] = sum / ch;
        }
        lock (_gate)
        {
            if (silent) Array.Clear(_mono, 0, frames);
            _analyzer?.Add(_mono.AsSpan(0, frames));
            _lastDataTicks = Stopwatch.GetTimestamp();
        }
    }

    public void Dispose() => Stop();
}
