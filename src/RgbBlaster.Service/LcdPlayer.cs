using RgbBlaster.Drivers.Lcd;

namespace RgbBlaster.Service;

/// <summary>
/// Feeds the pump LCD on its own thread: the status screen once per second, or media frames at their own timing.
/// A still image is sent again every second because the screen expects regular frames.
/// Works without a device (simulation); the last frame is kept for the GUI preview either way.
/// </summary>
public sealed class LcdPlayer : IDisposable
{
    private readonly LinkLcdDevice? _device;
    private readonly Func<(float? Cpu, float? Gpu, int? Pump)> _values;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _running = true;
    private volatile IReadOnlyList<LcdFrame>? _frames;

    public LcdPlayer(LinkLcdDevice? device, Func<(float? Cpu, float? Gpu, int? Pump)> values)
    {
        _device = device;
        _values = values;
        _thread = new Thread(Run) { IsBackground = true, Name = "LcdPlayer" };
        _thread.Start();
    }

    public bool HasDevice => _device is not null;
    public byte[]? LastFrame { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Null shows the status screen.</summary>
    public void Show(IReadOnlyList<LcdFrame>? frames)
    {
        _frames = frames is { Count: > 0 } ? frames : null;
        _wake.Set();
    }

    public void SetBrightness(byte percent) => Try(() => _device?.SetBrightness(percent));
    public void SetRotation(byte quarterTurns) => Try(() => _device?.SetRotation(quarterTurns));

    private void Run()
    {
        using var dash = new LcdDashboard();
        int index = 0;
        IReadOnlyList<LcdFrame>? playing = null;
        while (_running)
        {
            var frames = _frames;
            if (!ReferenceEquals(frames, playing)) { playing = frames; index = 0; }
            int delay;
            byte[] jpeg;
            if (playing is null)
            {
                var (cpu, gpu, pump) = _values();
                jpeg = dash.Render(cpu, gpu, pump);
                delay = 1000;
            }
            else
            {
                var f = playing[index % playing.Count];
                index++;
                jpeg = f.Jpeg;
                delay = playing.Count == 1 ? 1000 : f.DelayMs;
            }
            LastFrame = jpeg;
            Try(() => _device?.ShowJpeg(jpeg));
            _wake.WaitOne(delay);
        }
    }

    private void Try(Action a)
    {
        try { a(); LastError = null; }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread.Join(2000);
        if (_device is not null)
        {
            try { _device.Release(); } catch { /* best effort on shutdown */ }
            _device.Dispose();
        }
        _wake.Dispose();
    }
}
