using RgbBlaster.Drivers.Lcd;

namespace RgbBlaster.Service;

/// <summary>
/// Feeds the pump LCD on its own thread: the status screen once per second, or media frames at their own timing.
/// A still image is sent again every second because the screen expects regular frames.
/// All device I/O runs on that thread. After a fault the device is closed and opened again.
/// Works without a device (simulation); the last frame is kept for the GUI preview either way.
/// </summary>
public sealed class LcdPlayer : IDisposable
{
    private const int ReopenDelayMs = 2000;
    // Assumed power-on values. Feature reports are sent only when the requested value differs.
    private const byte DefaultBrightness = 100, DefaultRotation = 0;

    private readonly Func<LinkLcdDevice?>? _open;
    private readonly Func<(float? Cpu, float? Gpu, int? Pump)> _values;
    private readonly Action<string> _note;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _running = true;
    private volatile IReadOnlyList<LcdFrame>? _frames;
    private volatile string _screen = "status";
    private volatile byte _brightness = DefaultBrightness, _rotation = DefaultRotation;
    private LinkLcdDevice? _device;

    /// <param name="open">Opens the device; null runs without hardware.</param>
    private readonly Func<LcdBackground?> _background;

    /// <param name="background">Lighting canvas for the lighting screens; called only while one is shown.</param>
    public LcdPlayer(Func<LinkLcdDevice?>? open, Func<(float? Cpu, float? Gpu, int? Pump)> values, Action<string> note,
        Func<LcdBackground?>? background = null)
    {
        _background = background ?? (() => null);
        _open = open;
        _values = values;
        _note = note;
        _thread = new Thread(Run) { IsBackground = true, Name = "LcdPlayer" };
        _thread.Start();
    }

    public bool HasDevice => _device is not null;
    public byte[]? LastFrame { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Built-in screen shown while no media plays. See <see cref="LcdDashboard.Screens"/>.</summary>
    public void ShowScreen(string id) { _screen = id; _wake.Set(); }

    /// <summary>Null shows the built-in screen.</summary>
    public void Show(IReadOnlyList<LcdFrame>? frames)
    {
        _frames = frames is { Count: > 0 } ? frames : null;
        _wake.Set();
    }

    public void SetBrightness(byte percent) { _brightness = Math.Min(percent, (byte)100); _wake.Set(); }
    public void SetRotation(byte quarterTurns) { _rotation = (byte)(quarterTurns & 3); _wake.Set(); }

    private void Run()
    {
        using var dash = new LcdDashboard();
        int index = 0;
        IReadOnlyList<LcdFrame>? playing = null;
        byte sentBrightness = DefaultBrightness, sentRotation = DefaultRotation;
        bool openFailedNoted = false;
        while (_running)
        {
            if (_device is null && _open is not null)
            {
                try { _device = _open(); }
                catch (Exception ex) { Fault("open", ex); }
                if (_device is null)
                {
                    if (!openFailedNoted) { _note("LCD: not found; retrying."); openFailedNoted = true; }
                    _wake.WaitOne(ReopenDelayMs);
                    continue;
                }
                openFailedNoted = false;
                sentBrightness = DefaultBrightness;
                sentRotation = DefaultRotation;
            }

            var frames = _frames;
            if (!ReferenceEquals(frames, playing)) { playing = frames; index = 0; }
            int delay;
            byte[] jpeg;
            if (playing is null)
            {
                var (cpu, gpu, pump) = _values();
                var screen = _screen;
                var bg = LcdDashboard.UsesLighting(screen) ? _background() : null;
                jpeg = dash.Render(screen, new LcdValues(cpu, gpu, pump), bg);
                delay = LcdDashboard.FrameMs(screen);
            }
            else
            {
                var f = playing[index % playing.Count];
                index++;
                jpeg = f.Jpeg;
                delay = playing.Count == 1 ? 1000 : f.DelayMs;
            }
            LastFrame = jpeg;

            if (_device is { } dev)
            {
                string op = "frame";
                try
                {
                    byte b = _brightness, r = _rotation;
                    if (b != sentBrightness) { op = "brightness"; dev.SetBrightness(b); sentBrightness = b; }
                    if (r != sentRotation) { op = "rotation"; dev.SetRotation(r); sentRotation = r; }
                    op = "frame";
                    dev.ShowJpeg(jpeg);
                    LastError = null;
                }
                catch (Exception ex)
                {
                    Fault(op, ex);
                    dev.Dispose();
                    _device = null;
                    _wake.WaitOne(ReopenDelayMs);
                    continue;
                }
            }
            _wake.WaitOne(delay);
        }
    }

    private void Fault(string op, Exception ex)
    {
        LastError = $"{op}: {ex.Message}";
        _note($"LCD {op} failed: {ex.Message}");
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread.Join(3000);
        if (_device is { } dev)
        {
            try { dev.Release(); } catch { /* best effort on shutdown */ }
            dev.Dispose();
        }
        _wake.Dispose();
    }
}
