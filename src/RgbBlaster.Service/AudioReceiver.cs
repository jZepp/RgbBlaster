using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RgbBlaster.Core;

namespace RgbBlaster.Service;

/// <summary>
/// Receives sound levels from the tray app as UDP datagrams on 127.0.0.1 (see <see cref="AudioPacket"/>).
/// The service cannot capture audio itself: it runs in session 0, away from the user's playback device.
/// Levels older than <see cref="StaleAfter"/> count as silence.
/// </summary>
public sealed class AudioReceiver : IAudioSource, IDisposable
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(300);

    private readonly UdpClient? _udp;
    private readonly Thread? _thread;
    private volatile AudioFrame _frame = AudioFrame.Silence;
    private long _lastTicks;
    private volatile bool _running = true;

    public AudioReceiver(int port, Action<string> note)
    {
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _thread = new Thread(Run) { IsBackground = true, Name = "AudioReceiver" };
            _thread.Start();
        }
        catch (SocketException ex) { note($"Sound: cannot listen on UDP {port}: {ex.Message}"); }
    }

    /// <summary>True when levels arrived within the last second.</summary>
    public bool Receiving => _lastTicks != 0 && Stopwatch.GetElapsedTime(_lastTicks) < TimeSpan.FromSeconds(1);

    public AudioFrame Current => _lastTicks != 0 && Stopwatch.GetElapsedTime(_lastTicks) < StaleAfter ? _frame : AudioFrame.Silence;

    private void Run()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                var data = _udp!.Receive(ref from);
                if (AudioPacket.Decode(data) is { } f)
                {
                    _frame = f;
                    _lastTicks = Stopwatch.GetTimestamp();
                }
            }
            catch (SocketException) when (_running) { /* e.g. ICMP port unreachable echo; keep listening */ }
            catch (Exception) when (!_running) { return; }
        }
    }

    public void Dispose()
    {
        _running = false;
        _udp?.Dispose();
        _thread?.Join(500);
    }
}

/// <summary>Simulation: received levels when the tray app sends them, otherwise a steady 120 bpm beat with some treble.</summary>
public sealed class SimulatedAudio(AudioReceiver live) : IAudioSource
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public AudioFrame Current
    {
        get
        {
            if (live.Receiving) return live.Current;
            double t = _clock.Elapsed.TotalSeconds;
            float kick = MathF.Exp(-(float)(t % 0.5) * 9);
            var bands = new float[AudioFrame.BandCount];
            for (int i = 0; i < bands.Length; i++)
                bands[i] = Math.Clamp(i < 3 ? kick : 0.25f + 0.25f * MathF.Sin((float)t * (1 + i * 0.37f) + i), 0, 1);
            return new AudioFrame(0.4f + 0.5f * kick, bands);
        }
    }
}
