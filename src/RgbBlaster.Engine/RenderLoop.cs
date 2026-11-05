using System.Diagnostics;
using RgbBlaster.Core;

namespace RgbBlaster.Engine;

/// <summary>
/// Renders the active effect at a fixed rate and hands each device its zone colors.
/// Each device has its own output thread, so a slow USB device cannot stall the frame or other devices.
/// Output is latest-frame-wins: a device that falls behind skips frames instead of queuing them.
/// </summary>
public sealed class RenderLoop : IDisposable
{
    private sealed record Binding(int ZoneIndex, int[] PixelIndex, Rgb[] Scratch);

    private readonly Canvas _canvas;
    private readonly int _frameRate;
    private readonly List<(IRgbDevice Device, Binding[] Zones, DeviceWorker Worker)> _targets = [];
    private volatile IEffect _effect;
    private Thread? _thread;
    private volatile bool _running;

    public RenderLoop(AppConfig config, IEnumerable<IRgbDevice> devices, IEffect effect)
    {
        _canvas = new Canvas(config.CanvasWidth, config.CanvasHeight);
        _frameRate = Math.Clamp(config.FrameRate, 1, 240);
        _effect = effect;

        foreach (var dev in devices)
        {
            var zones = new List<Binding>();
            for (int z = 0; z < dev.Zones.Count; z++)
            {
                var info = dev.Zones[z];
                var place = config.Layout.FirstOrDefault(p =>
                    p.Device.Equals(dev.Id, StringComparison.OrdinalIgnoreCase) &&
                    p.Zone.Equals(info.Id, StringComparison.OrdinalIgnoreCase));
                // Unplaced zones sample the canvas centre so they still follow the effect.
                place ??= new ZonePlacement { Shape = PlacementShape.Line, X = 0.5f, Y = 0.5f, X2 = 0.5f, Y2 = 0.5f };
                zones.Add(new Binding(z, LayoutMapper.Map(place, info.LedCount, _canvas.Width, _canvas.Height, info.Positions), new Rgb[info.LedCount]));
            }
            _targets.Add((dev, zones.ToArray(), new DeviceWorker(dev)));
        }
    }

    public IEffect Effect { get => _effect; set => _effect = value; }

    public double LastRenderMs { get; private set; }

    /// <summary>
    /// Downsampled copy of the current canvas as packed RGB bytes, row by row, for previews.
    /// Reads the canvas while the render thread may be writing it; a preview can mix two frames.
    /// </summary>
    public byte[] Preview(int width, int height) => PreviewRegion(0, 0, 1, 1, width, height);

    /// <summary>Like <see cref="Preview"/>, for the canvas area x0..x1, y0..y1 (0..1).</summary>
    public byte[] PreviewRegion(float x0, float y0, float x1, float y1, int width, int height)
    {
        int cw = _canvas.Width, ch = _canvas.Height;
        var src = _canvas.Pixels;
        var dst = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Clamp((int)((y0 + (y1 - y0) * (y + 0.5f) / height) * ch), 0, ch - 1);
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Clamp((int)((x0 + (x1 - x0) * (x + 0.5f) / width) * cw), 0, cw - 1);
                var c = src[sy * cw + sx];
                int o = (y * width + x) * 3;
                dst[o] = c.R; dst[o + 1] = c.G; dst[o + 2] = c.B;
            }
        }
        return dst;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "RenderLoop", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        long frameTicks = Stopwatch.Frequency / _frameRate;
        long next = clock.ElapsedTicks;

        IEffect? notified = null;
        while (_running)
        {
            if (!ReferenceEquals(notified, _effect))
            {
                notified = _effect;
                foreach (var (dev, _, _) in _targets)
                    if (dev is INativeEffectDevice n) n.SetNativeEffect(notified.Native);
            }
            long start = clock.ElapsedTicks;
            _effect.Render(_canvas, (double)start / Stopwatch.Frequency);
            var pixels = _canvas.Pixels;
            foreach (var (dev, zones, worker) in _targets)
            {
                foreach (var b in zones)
                {
                    var idx = b.PixelIndex;
                    var dst = b.Scratch;
                    for (int i = 0; i < idx.Length; i++) dst[i] = pixels[idx[i]];
                    dev.SetZone(b.ZoneIndex, dst);
                }
                worker.Signal();
            }
            LastRenderMs = (clock.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency;

            next += frameTicks;
            long wait = next - clock.ElapsedTicks;
            if (wait < 0) { next = clock.ElapsedTicks; continue; } // overran; drop the backlog
            int ms = (int)(wait * 1000 / Stopwatch.Frequency);
            if (ms > 1) Thread.Sleep(ms - 1);
            while (clock.ElapsedTicks < next) Thread.SpinWait(50);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
        foreach (var t in _targets) t.Worker.Dispose();
    }
}

/// <summary>Flushes one device on its own thread when signalled.</summary>
public sealed class DeviceWorker : IDisposable
{
    private readonly IRgbDevice _device;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;

    public DeviceWorker(IRgbDevice device)
    {
        _device = device;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Out:{device.Id}" };
        _thread.Start();
    }

    public long Errors { get; private set; }
    public Exception? LastError { get; private set; }

    public void Signal() => _signal.Set();

    private void Run()
    {
        while (_running)
        {
            _signal.WaitOne();
            if (!_running) break;
            try { _device.Flush(); }
            catch (Exception ex) { Errors++; LastError = ex; }
        }
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _thread.Join(1000);
        _signal.Dispose();
    }
}
