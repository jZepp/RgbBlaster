using RgbBlaster.Core;
using RgbBlaster.Drivers;
using RgbBlaster.Drivers.Lcd;
using RgbBlaster.Drivers.Link;
using RgbBlaster.Drivers.SuperIo;
using RgbBlaster.Engine;

namespace RgbBlaster.Service;

/// <summary>
/// Owns every device for the life of the service: lighting, fan curves, the pump LCD.
/// A one-second tick on the service thread reads sensors, runs the fan curves and polls the LINK hub. LibreHardwareMonitor
/// is not thread-safe, so only that tick touches it; requests read the cached values.
/// Changes from the GUI take the <see cref="_gate"/> lock and are saved to settings.json.
/// </summary>
public sealed class RgbRuntime(AppConfig config, SettingsStore store, bool simulate, ILogger<RgbRuntime> log) : BackgroundService
{
    private const int HistorySeconds = 300;

    private readonly object _gate = new();
    private RuntimeSettings _settings = new();
    private List<IRgbDevice> _devices = [];
    private List<LinkHubDevice> _hubs = [];
    private List<IFanChannel> _fans = [];
    private RenderLoop? _loop;
    private FanController? _fanCtl;
    private HardwareMonitorSession? _hm;
    private BoardFanDevice? _board;
    private ITemperatureSource? _temps;
    private LcdPlayer? _lcd;
    private readonly Dictionary<string, IReadOnlyList<LcdFrame>> _mediaCache = [];

    // Cached by the tick for request threads.
    private volatile StatusSnapshot _status = StatusSnapshot.Empty;
    private readonly Queue<HistoryPoint> _history = new();
    private IReadOnlyList<SensorInfo> _sensors = [];
    private readonly List<string> _messages = [];
    private string? _lastNote;
    private int _repeats;

    public bool Simulated => simulate;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield();
        try
        {
            Start();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int tick = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stop))
            {
                double dt = clock.Elapsed.TotalSeconds;
                clock.Restart();
                Tick(dt, tick++ % 10 == 0);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Shutdown();
        }
    }

    private void Start()
    {
        _settings = store.Load(config);
        var profile = ActiveProfileUnlocked();

        if (simulate)
        {
            _devices = Simulation.Devices(_settings.Layout);
            _temps = new Simulation.Temps();
            _fans = Simulation.Fans(_settings.Profiles.SelectMany(p => p.FanCurves).SelectMany(c => c.Fans));
            _lcd = new LcdPlayer(null, LcdValues);
            Note("Simulation: no hardware is touched.");
        }
        else
        {
            _devices = DeviceDiscovery.OpenAll(config, Note);
            _hubs = _devices.OfType<LinkHubDevice>().ToList();
            _hm = new HardwareMonitorSession();
            try { _board = _hm.OpenBoardFans(config.BoardFans); }
            catch (Exception ex) { Note($"Board fans: {ex.Message}"); }
            if (config.BoardFans.Count > 0 && _board is null) Note("Board fans: no Super I/O access (PawnIO and administrator rights needed). They stay on the BIOS curve.");
            _temps = _hm;
            _fans = _hubs.SelectMany(h => h.Fans).Concat(_board?.Fans ?? []).ToList();
            LinkLcdDevice? lcd = null;
            try { lcd = LinkLcdDevice.Discover().FirstOrDefault(); }
            catch (Exception ex) { Note($"LCD: {ex.Message}"); }
            _lcd = new LcdPlayer(lcd, LcdValues);
        }

        _fanCtl = BuildFanController(profile.FanCurves);
        _loop = new RenderLoop(LoopConfig(), _devices, EffectFactory.Create(profile.Effect));
        _loop.Start();
        ApplyLcdUnlocked(profile.Lcd);
        Note($"Started with {_devices.Count} lighting device(s), {_fans.Count} fan channel(s).");
    }

    private void Tick(double dt, bool refreshSensors)
    {
        lock (_gate)
        {
            try { _fanCtl?.Tick(dt); }
            catch (Exception ex) { Note($"Fan curves: {ex.Message}"); }
        }
        foreach (var h in _hubs)
        {
            try { h.Poll(); }
            catch (Exception ex) { Note($"LINK poll: {ex.Message}"); }
        }
        float? cpu = _temps?.ReadCelsius("cpu"), gpu = _temps?.ReadCelsius("gpu");
        var fans = _fans.Select(f => new FanInfo(f.Id, f.Name, f.IsPump, SafeRpm(f))).ToList();
        IReadOnlyList<FanCurveState> curves;
        lock (_gate) curves = _fanCtl?.States() ?? [];
        if (refreshSensors && _hm is not null)
        {
            try { _sensors = _hm.Temperatures().Where(t => t.Value is > 0).Select(t => new SensorInfo(t.Id, t.Hardware, t.Name, t.Value)).ToList(); }
            catch (Exception ex) { Note($"Sensors: {ex.Message}"); }
        }

        var point = new HistoryPoint(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cpu, gpu, curves.ToDictionary(c => c.Name, c => (int?)c.Duty));
        lock (_history)
        {
            _history.Enqueue(point);
            while (_history.Count > HistorySeconds) _history.Dequeue();
        }
        _status = new StatusSnapshot(cpu, gpu, fans, curves, _loop?.LastRenderMs ?? 0);
    }

    private static int? SafeRpm(IFanChannel f)
    {
        try { return f.ReadRpm(); } catch { return null; }
    }

    private (float?, float?, int?) LcdValues()
    {
        var s = _status;
        return (s.Cpu, s.Gpu, s.Fans.FirstOrDefault(f => f.IsPump)?.Rpm);
    }

    private void Shutdown()
    {
        lock (_gate)
        {
            _loop?.Dispose();
            _loop = null;
            try { _board?.Release(); } catch (Exception ex) { log.LogWarning("Board fan release: {Message}", ex.Message); }
            _lcd?.Dispose();
            _lcd = null;
            ExitLighting(_devices, config.OnExit);
            _hm?.Dispose();
        }
    }

    private static void ExitLighting(List<IRgbDevice> devices, ExitLighting mode)
    {
        foreach (var d in devices)
        {
            try
            {
                switch (mode)
                {
                    case Core.ExitLighting.Firmware: d.Release(); break;
                    case Core.ExitLighting.Off:
                        for (int z = 0; z < d.Zones.Count; z++) d.SetZone(z, new Rgb[d.Zones[z].LedCount]);
                        d.Flush();
                        break;
                    case Core.ExitLighting.SaveLast when d is IPersistentLighting p: p.SaveLightingAndRelease(); break;
                }
            }
            catch { /* best effort on shutdown */ }
            d.Dispose();
        }
        devices.Clear();
    }

    private AppConfig LoopConfig() => new()
    {
        FrameRate = config.FrameRate,
        CanvasWidth = config.CanvasWidth,
        CanvasHeight = config.CanvasHeight,
        Layout = _settings.Layout,
    };

    private FanController BuildFanController(List<FanCurveConfig> curves)
    {
        var ctl = new FanController(curves, _fans, _temps ?? new Simulation.Temps());
        foreach (var w in ctl.Warnings) Note($"Fan curves: {w}");
        return ctl;
    }

    private void Note(string message)
    {
        lock (_messages)
        {
            // A fault that repeats every tick is logged once until a different message appears.
            if (message == _lastNote) { _repeats++; return; }
            if (_repeats > 0) _messages.Add($"{DateTime.Now:HH:mm:ss} (previous message repeated {_repeats} times)");
            _lastNote = message;
            _repeats = 0;
            log.LogInformation("{Message}", message);
            _messages.Add($"{DateTime.Now:HH:mm:ss} {message}");
            if (_messages.Count > 50) _messages.RemoveAt(0);
        }
    }

    private Profile ActiveProfileUnlocked() => _settings.Profiles.First(p => p.Name == _settings.ActiveProfile);

    // ---- Read side ----

    public object Status()
    {
        var s = _status;
        lock (_gate)
            return new
            {
                simulated = simulate,
                activeProfile = _settings.ActiveProfile,
                cpu = s.Cpu,
                gpu = s.Gpu,
                renderMs = s.RenderMs,
                fans = s.Fans,
                curves = s.Curves,
                lcd = new { present = _lcd?.HasDevice ?? false, error = _lcd?.LastError },
                devices = _devices.Select(d => new { d.Id, d.Name, kind = d.Kind.ToString(), zones = d.Zones.Select(z => new { z.Id, z.Name, leds = z.LedCount }) }),
            };
    }

    public IReadOnlyList<HistoryPoint> History()
    {
        lock (_history) return _history.ToList();
    }

    public IReadOnlyList<string> Messages()
    {
        lock (_messages) return _messages.ToList();
    }

    public IReadOnlyList<SensorInfo> Sensors() => _sensors;

    public RuntimeSettings Settings()
    {
        lock (_gate) return _settings;
    }

    public byte[] Preview(int w, int h)
    {
        lock (_gate) return _loop?.Preview(w, h) ?? new byte[w * h * 3];
    }

    public byte[]? LcdFrame() => _lcd?.LastFrame;

    /// <summary>Canvas positions (0..1) of every LED, per placed zone, for the layout editor.</summary>
    public object LedPoints(List<ZonePlacement>? layout = null)
    {
        lock (_gate)
        {
            layout ??= _settings.Layout;
            int w = config.CanvasWidth, h = config.CanvasHeight;
            var zones = new List<object>();
            foreach (var d in _devices)
                foreach (var z in d.Zones)
                {
                    var p = layout.FirstOrDefault(x => x.Device.Equals(d.Id, StringComparison.OrdinalIgnoreCase) && x.Zone.Equals(z.Id, StringComparison.OrdinalIgnoreCase));
                    if (p is null) { zones.Add(new { device = d.Id, zone = z.Id, name = z.Name, placed = false, points = Array.Empty<float[]>() }); continue; }
                    var idx = LayoutMapper.Map(p, z.LedCount, w, h, z.Positions);
                    zones.Add(new { device = d.Id, zone = z.Id, name = z.Name, placed = true, points = idx.Select(i => new[] { (i % w + 0.5f) / w, (i / w + 0.5f) / h }).ToArray() });
                }
            return zones;
        }
    }

    // ---- Write side ----

    public void SetEffect(EffectSpec spec)
    {
        var effect = EffectFactory.Create(spec);
        lock (_gate)
        {
            ActiveProfileUnlocked().Effect = spec;
            if (_loop is not null) _loop.Effect = effect;
            store.Save(_settings);
        }
    }

    public IReadOnlyList<string> SetFanCurves(List<FanCurveConfig> curves)
    {
        foreach (var c in curves)
        {
            if (c.Points.Count == 0) throw new ArgumentException($"Curve '{c.Name}' has no points.");
            foreach (var p in c.Points)
                if (p.Duty > 100 || p.Temp is < 0 or > 120) throw new ArgumentException($"Curve '{c.Name}': point {p.Temp}°C {p.Duty}% is out of range.");
        }
        lock (_gate)
        {
            _fanCtl = BuildFanController(curves);
            ActiveProfileUnlocked().FanCurves = curves;
            store.Save(_settings);
            return _fanCtl.Warnings;
        }
    }

    public void SetLayout(List<ZonePlacement> layout)
    {
        lock (_gate)
        {
            _settings.Layout = layout;
            var effect = _loop?.Effect ?? EffectFactory.Create(ActiveProfileUnlocked().Effect);
            _loop?.Dispose();
            _loop = new RenderLoop(LoopConfig(), _devices, effect);
            _loop.Start();
            store.Save(_settings);
        }
    }

    public void SetLcd(LcdSettings lcd)
    {
        lock (_gate)
        {
            ApplyLcdUnlocked(lcd);
            ActiveProfileUnlocked().Lcd = lcd;
            store.Save(_settings);
        }
    }

    private void ApplyLcdUnlocked(LcdSettings lcd)
    {
        if (_lcd is null) return;
        _lcd.SetBrightness(Math.Min(lcd.Brightness, (byte)100));
        _lcd.SetRotation((byte)(lcd.Rotation & 3));
        IReadOnlyList<LcdFrame>? frames = null;
        if (lcd.Mode == "media" && lcd.Media is { } id)
        {
            frames = LoadMedia(id);
            if (frames is null) Note($"LCD: media '{id}' not found; showing the status screen.");
        }
        _lcd.Show(frames);
    }

    private IReadOnlyList<LcdFrame>? LoadMedia(string id)
    {
        if (_mediaCache.TryGetValue(id, out var f)) return f;
        var path = MediaPath(id);
        if (path is null) return null;
        using var fs = File.OpenRead(path);
        return _mediaCache[id] = LcdMedia.Decode(fs);
    }

    public string? MediaPath(string id)
    {
        if (id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '.')) return null;
        var path = Path.Combine(store.MediaDirectory, id);
        return File.Exists(path) ? path : null;
    }

    public IEnumerable<object> MediaList() =>
        Directory.EnumerateFiles(store.MediaDirectory)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new { id = f.Name, animated = f.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase), bytes = f.Length });

    /// <summary>Stores an uploaded image after checking that it decodes. Returns its id.</summary>
    public string AddMedia(Stream data, string extension)
    {
        using var ms = new MemoryStream();
        data.CopyTo(ms);
        ms.Position = 0;
        var frames = LcdMedia.Decode(ms); // throws on files that are not images
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{extension}";
        File.WriteAllBytes(Path.Combine(store.MediaDirectory, id), ms.ToArray());
        lock (_gate) _mediaCache[id] = frames;
        return id;
    }

    public void DeleteMedia(string id)
    {
        var path = MediaPath(id) ?? throw new KeyNotFoundException(id);
        lock (_gate)
        {
            if (ActiveProfileUnlocked().Lcd is { Mode: "media" } l && l.Media == id) throw new InvalidOperationException("The LCD is showing this file. Pick another first.");
            _mediaCache.Remove(id);
            File.Delete(path);
        }
    }

    public void SaveProfileAs(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40) throw new ArgumentException("Profile names are 1 to 40 characters.");
        lock (_gate)
        {
            var cur = ActiveProfileUnlocked();
            var copy = new Profile
            {
                Name = name,
                Effect = cur.Effect.Clone(),
                FanCurves = System.Text.Json.JsonSerializer.Deserialize<List<FanCurveConfig>>(System.Text.Json.JsonSerializer.Serialize(cur.FanCurves))!,
                Lcd = new LcdSettings { Mode = cur.Lcd.Mode, Media = cur.Lcd.Media, Brightness = cur.Lcd.Brightness, Rotation = cur.Lcd.Rotation },
            };
            _settings.Profiles.RemoveAll(p => p.Name == name);
            _settings.Profiles.Add(copy);
            _settings.ActiveProfile = name;
            store.Save(_settings);
        }
    }

    public void ActivateProfile(string name)
    {
        lock (_gate)
        {
            var p = _settings.Profiles.FirstOrDefault(x => x.Name == name) ?? throw new KeyNotFoundException(name);
            _settings.ActiveProfile = name;
            if (_loop is not null) _loop.Effect = EffectFactory.Create(p.Effect);
            _fanCtl = BuildFanController(p.FanCurves);
            ApplyLcdUnlocked(p.Lcd);
            store.Save(_settings);
        }
    }

    public void DeleteProfile(string name)
    {
        lock (_gate)
        {
            if (_settings.Profiles.Count == 1) throw new InvalidOperationException("The last profile cannot be deleted.");
            if (_settings.ActiveProfile == name) throw new InvalidOperationException("Activate another profile first.");
            if (_settings.Profiles.RemoveAll(p => p.Name == name) == 0) throw new KeyNotFoundException(name);
            store.Save(_settings);
        }
    }
}

public sealed record FanInfo(string Id, string Name, bool IsPump, int? Rpm);
public sealed record SensorInfo(string Id, string Hardware, string Name, float? Value);
public sealed record HistoryPoint(long T, float? Cpu, float? Gpu, Dictionary<string, int?> Duty);

internal sealed record StatusSnapshot(float? Cpu, float? Gpu, IReadOnlyList<FanInfo> Fans, IReadOnlyList<FanCurveState> Curves, double RenderMs)
{
    public static readonly StatusSnapshot Empty = new(null, null, [], [], 0);
}
