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
    private readonly Func<LcdValues> _values;
    private readonly Action<string> _note;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _running = true;
    private volatile IReadOnlyList<LcdFrame>? _frames;
    private volatile string _screen = "status";
    private volatile bool _handBack;
    private volatile byte _brightness = DefaultBrightness, _rotation = DefaultRotation;
    private LinkLcdDevice? _device;

    /// <param name="open">Opens the device; null runs without hardware.</param>
    private readonly Func<LcdBackground?> _background;

    /// <param name="background">Lighting canvas for the lighting screens; called only while one is shown.</param>
    public LcdPlayer(Func<LinkLcdDevice?>? open, Func<LcdValues> values, Action<string> note,
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

    private volatile LcdBackgroundStyle _style = LcdBackgroundStyle.Off;
    public void SetBackground(LcdBackgroundStyle style) { _style = style; _wake.Set(); }

    private volatile IReadOnlyDictionary<string, List<string>> _data = new Dictionary<string, List<string>>();
    /// <summary>Chosen metric ids per screen; see <see cref="LcdDashboard.DefaultSlots"/>.</summary>
    public void SetData(IReadOnlyDictionary<string, List<string>> data) { _data = data; _wake.Set(); }

    private volatile bool _overlay;
    /// <summary>True draws the temperatures over media frames.</summary>
    public void SetOverlay(bool on) { _overlay = on; _wake.Set(); }

    /// <summary>Null shows the built-in screen.</summary>
    public void Show(IReadOnlyList<LcdFrame>? frames)
    {
        _frames = frames is { Count: > 0 } ? frames : null;
        _wake.Set();
    }

    public void SetBrightness(byte percent) { _brightness = Math.Min(percent, (byte)100); _wake.Set(); }
    public void SetRotation(byte quarterTurns) { _rotation = (byte)(quarterTurns & 3); _wake.Set(); }

    private volatile int _minFrameMs = 1000;
    /// <summary>Limits frames sent to the device. Media skips frames to keep its speed; screens render less often.</summary>
    public void SetMaxFps(float fps) { _minFrameMs = (int)(1000 / Math.Clamp(float.IsFinite(fps) ? fps : 1, 0.5f, 10f)); _wake.Set(); }

    /// <summary>True returns the screen to the AIO's own content and stops sending frames; false takes it back.</summary>
    public void HandBack(bool on) { _handBack = on; _wake.Set(); }

    private void Run()
    {
        using var dash = new LcdDashboard();
        int index = 0;
        IReadOnlyList<LcdFrame>? playing = null;
        byte sentBrightness = DefaultBrightness, sentRotation = DefaultRotation;
        bool openFailedNoted = false, released = false;
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
                released = false;
                sentBrightness = DefaultBrightness;
                sentRotation = DefaultRotation;
            }

            if (_handBack)
            {
                if (!released && _device is { } d)
                {
                    try { d.Release(); released = true; LastError = null; }
                    catch (Exception ex) { Fault("hand back", ex); d.Dispose(); _device = null; }
                }
                LastFrame = null;
                _wake.WaitOne(1000);
                continue;
            }
            released = false;

            var frames = _frames;
            if (!ReferenceEquals(frames, playing)) { playing = frames; index = 0; }
            int delay;
            byte[] jpeg;
            if (playing is null)
            {
                var screen = _screen;
                var style = _style;
                var bg = LcdDashboard.NeedsBackground(screen, style) ? _background() : null;
                jpeg = dash.Render(screen, _values(), bg, style, _data.GetValueOrDefault(screen));
                delay = Math.Max(LcdDashboard.FrameMs(screen, style), _minFrameMs);
            }
            else
            {
                var f = playing[index % playing.Count];
                index++;
                jpeg = f.Jpeg;
                delay = playing.Count == 1 ? 1000 : f.DelayMs;
                // Skip frames that would come too soon, so the animation keeps its speed at the lower rate.
                while (playing.Count > 1 && delay < _minFrameMs) { delay += Math.Max(1, playing[index % playing.Count].DelayMs); index++; }
                if (_overlay)
                {
                    try { jpeg = dash.Overlay(jpeg, _values()); }
                    catch (Exception ex) { Fault("overlay", ex); }
                }
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
