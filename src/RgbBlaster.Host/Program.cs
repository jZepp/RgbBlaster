using RgbBlaster.Core;
using RgbBlaster.Drivers.Fusion2;
using RgbBlaster.Drivers.EVision;
using RgbBlaster.Drivers.Gpu;
using RgbBlaster.Drivers.Logitech;
using RgbBlaster.Drivers.Lcd;
using RgbBlaster.Drivers.Link;
using RgbBlaster.Drivers.SuperIo;
using RgbBlaster.Engine;

var configPath = Path.Combine(AppContext.BaseDirectory, "rgbblaster.json");
var config = AppConfig.Load(configPath);
var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (cmd)
{
    case "info": return Info();
    case "run": return Run(args.Skip(1).ToArray());
    case "calibrate": return Calibrate(args.Skip(1).ToArray());
    case "link-info": return LinkInfo();
    case "identify": return Identify.Run(args.Skip(1).ToArray(), config);
    case "keyboard-config": return KeyboardConfig();
    case "keyboard-poke": return KeyboardPoke(args.Skip(1).ToArray());
    case "board-fans": return BoardFans();
    case "temps": return Temps();
    case "lcd-test": return LcdTest(args.Skip(1).ToArray());
    case "board-fan-test": return BoardFanTest(args.Skip(1).ToArray());
    case "gpu-fan-test": return GpuFanTest(args.Skip(1).ToArray());
    case "gpu-ring-test": return GpuRingTest(args.Skip(1).ToArray());
    default:
        Console.WriteLine("""
            rgbblaster identify lights|fans|walk Map zones and fan channels to physical parts
            rgbblaster info                      Show detected Fusion 2 controllers (reads only)
            rgbblaster board-fans                Super I/O fan RPM, PWM and temperatures (reads only; needs PawnIO, run as admin)
            rgbblaster board-fan-test <ch> [duty%..] Runs one header at each duty for 8 s (default 100 40), then back to BIOS (admin)
            rgbblaster gpu-fan-test [duty%] [s] Runs the GPU fans at duty (default 60) for s seconds (default 20), then back to the card
            rgbblaster gpu-ring-test [duty%..]   Steps the GPU fans up (default 0 30 40 50 60 80 100) and blinks the GPU light 1 s on, 1 s off at each step (admin)
            rgbblaster temps                     Every temperature sensor and its id, for fan curves (reads only; admin)
            rgbblaster lcd-test [image]          Shows the status screen (or an image) on the pump LCD for 20 s, then hands it back
            rgbblaster link-info                 LINK hub devices, RPM, temperatures (sets fans 50%, pump 100% while open)
            rgbblaster run solid <RRGGBB>        Static color on every zone
            rgbblaster run rainbow               Rainbow wave across the canvas
            rgbblaster run breathe <RRGGBB>      Breathing color
            rgbblaster calibrate <D_LED1..>      Light one LED at a time to count a header's LEDs
            """);
        return 0;
}

int Info()
{
    int found = 0;
    foreach (var dev in Fusion2Device.Discover(config.Fusion2))
    {
        using (dev)
        {
            var i = dev.ReadInfo();
            found++;
            Console.WriteLine($"{dev.Name}  fw {i.Firmware}  chip 0x{i.ChipId:X8}  flags 0x{i.SupportFlags:X2}  strip_detect {i.StripDetect}");
            for (int s = 0; s < 6; s++)
                Console.WriteLine($"  {(Fusion2Header)s,-9} count bucket {i.CountNibbles[s]}  calibration 0x{i.Calibration[s]:X8} -> {Fusion2Device.ResolveOrder(ColorOrderSetting.Auto, i.Calibration[s])}");
        }
    }
    if (found == 0) Console.WriteLine("No Fusion 2 USB controller found, or it is held by another program.");
    foreach (var g in NvApi.EnumerateGpus())
    {
        var known = g.SubVendor == GigabyteGpuProtocol.GigabyteSubVendor && g.Device == GigabyteGpuProtocol.Rtx5070TiDevice
            && GigabyteGpuProtocol.Rtx5070Ti.TryGetValue(g.SubDevice, out var card) ? $"{card.Name}, {card.Layout}" : "no RGB support";
        Console.WriteLine($"GPU {g.Vendor:X4}:{g.Device:X4} sub {g.SubVendor:X4}:{g.SubDevice:X4}  {known}");
    }
    return found == 0 ? 1 : 0;
}

int Run(string[] a)
{
    IEffect effect = (a.FirstOrDefault() ?? "rainbow") switch
    {
        "solid" => new SolidEffect(Rgb.Parse(a.ElementAtOrDefault(1) ?? "FFFFFF")),
        "breathe" => new BreathingEffect(Rgb.Parse(a.ElementAtOrDefault(1) ?? "00A0FF")),
        _ => new RainbowWaveEffect(),
    };

    var devices = OpenAll();
    if (devices.Count == 0) { Console.WriteLine("No devices."); return 1; }
    var hubs = devices.OfType<LinkHubDevice>().ToList();

    using var done = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };

    // Fans: LINK hub channels plus motherboard headers (needs PawnIO and admin), driven by the configured curves.
    using var hm = new HardwareMonitorSession();
    BoardFanDevice? board = null;
    try { board = hm.OpenBoardFans(config.BoardFans); }
    catch (Exception ex) { Console.WriteLine($"Board fans: {ex.Message}"); }
    if (config.BoardFans.Count > 0 && board is null) Console.WriteLine("Board fans: no Super I/O access (install PawnIO, run as administrator). They stay on the BIOS curve.");
    AppDomain.CurrentDomain.UnhandledException += (_, _) => board?.Release();
    var fans = hubs.SelectMany(h => h.Fans).Concat(board?.Fans ?? []).ToList();
    Console.WriteLine("Fans: " + string.Join(", ", fans.Select(f => $"{f.Id} ({f.Name})")));
    var fanCtl = new FanController(config.FanCurves, fans, hm);
    foreach (var w in fanCtl.Warnings) Console.WriteLine($"Fan curves: {w}");

    // Pump cap LCD: status screen refreshed each second.
    var lcd = OpenLcd();
    using var dash = lcd is null ? null : new LcdDashboard();
    var pump = fans.FirstOrDefault(f => f.IsPump);

    using (var loop = new RenderLoop(config, devices, effect))
    {
        loop.Start();
        Console.WriteLine($"Running '{effect.Name}' at {config.FrameRate} fps on {devices.Count} device(s). Ctrl+C to stop.");
        var tick = System.Diagnostics.Stopwatch.StartNew();
        while (!done.Wait(1000))
        {
            try { fanCtl.Tick(tick.Elapsed.TotalSeconds); }
            catch (Exception ex) { Console.WriteLine($"Fan curves: {ex.Message}"); }
            tick.Restart();
            foreach (var h in hubs)
            {
                try { h.Poll(); }
                catch (Exception ex) { Console.WriteLine($"LINK poll: {ex.Message}"); }
            }
            if (lcd is not null && dash is not null)
            {
                try { lcd.ShowJpeg(dash.Render(hm.ReadCelsius("cpu"), hm.ReadCelsius("gpu"), pump?.ReadRpm())); }
                catch (Exception ex) { Console.WriteLine($"LCD: {ex.Message}"); }
            }
            var rpm = string.Join(" ", fans.Select(f => $"{f.Id}:{f.ReadRpm()?.ToString() ?? "-"}"));
            Console.WriteLine($"render {loop.LastRenderMs:F2} ms  {string.Join("  ", fanCtl.Status())}  {rpm}");
        }
    }
    board?.Release();
    if (lcd is not null)
    {
        try { lcd.Release(); } catch { /* best effort on shutdown */ }
        lcd.Dispose();
    }

    ExitLightingRunner.Apply(devices, config.OnExit);
    return 0;
}

List<IRgbDevice> OpenAll()
{
    var list = RgbBlaster.Drivers.DeviceDiscovery.OpenAll(config, Console.WriteLine);
    // Hand devices back to firmware control even if the process dies on an unhandled exception.
    AppDomain.CurrentDomain.UnhandledException += (_, _) => ReleaseAll(list);
    return list;
}

static void ReleaseAll(List<IRgbDevice> devices)
{
    foreach (var d in devices)
    {
        try { d.Release(); } catch { /* best effort on shutdown */ }
        d.Dispose();
    }
    devices.Clear();
}

// Read-only: prints the keyboard's config bytes 0x00-0x37 and 0x38-0x6F as the replies return them.
int KeyboardConfig()
{
    var kb = EVisionKeyboardDevice.Discover().FirstOrDefault();
    if (kb is null) { Console.WriteLine("No EVision keyboard."); return 1; }
    using (kb)
        foreach (byte addr in new byte[] { 0x00, 0x38 })
        {
            var r = kb.ReadConfig(addr, 0x38);
            Console.WriteLine($"read 0x{addr:X2}: header {Convert.ToHexString(r.AsSpan(0, 8))}");
            for (int i = 8; i < r.Length; i += 16)
                Console.WriteLine($"  +0x{addr + i - 8:X2}: {Convert.ToHexString(r.AsSpan(i, Math.Min(16, r.Length - i)))}");
        }
    return 0;
}

// Writes raw keyboard config bytes: keyboard-poke <address hex> <data hex>, e.g. keyboard-poke 2A 06. Prints the block after.
int KeyboardPoke(string[] a)
{
    if (a.Length < 2) { Console.WriteLine("Usage: rgbblaster keyboard-poke <address hex> <data hex>"); return 2; }
    byte addr = Convert.ToByte(a[0], 16);
    var data = Convert.FromHexString(a[1]);
    var kb = EVisionKeyboardDevice.Discover().FirstOrDefault();
    if (kb is null) { Console.WriteLine("No EVision keyboard."); return 1; }
    using (kb)
    {
        kb.WriteConfig(addr, data);
        var r = kb.ReadConfig(addr, 16);
        Console.WriteLine($"0x{addr:X2} now: {Convert.ToHexString(r.AsSpan(8, 16))}");
    }
    return 0;
}

int LinkInfo()
{
    int found = 0;
    foreach (var hub in LinkHubDevice.Discover())
    {
        using (hub)
        {
            found++;
            try
            {
                hub.Open();
                Console.WriteLine($"{hub.Name}  fw {hub.Firmware}");
                foreach (var d in hub.SubDevices)
                    Console.WriteLine($"  ch {d.Channel,2}  type 0x{d.Type:X2} model 0x{d.Model:X2}  {d.Info?.Name ?? "unknown"}  leds {d.Info?.Leds.ToString() ?? "?"}");
                foreach (var f in hub.Fans)
                    Console.WriteLine($"  {f.Name}: {f.ReadRpm()?.ToString() ?? "-"} rpm");
                for (int i = 0; i < hub.Temperatures.Count; i++)
                    if (hub.Temperatures[i] is float t) Console.WriteLine($"  temp[{i}] {t:F1} °C");
                if (args.Contains("--raw"))
                {
                    // Serial numbers sit in this data. Print only sizes and header bytes.
                    var (p, c) = hub.RawDeviceList;
                    Console.WriteLine($"  raw: primary {p.Length} B, continuation {c.Length} B, header {Convert.ToHexString(p.AsSpan(0, 7))}, last channel {p[6]}");
                    if (c.Length > 0) Console.WriteLine($"  raw: continuation header {Convert.ToHexString(c.AsSpan(0, Math.Min(8, c.Length)))}");
                }
            }
            finally { hub.Release(); }
        }
    }
    if (found == 0) Console.WriteLine("No LINK hub found, or it is held by another program.");
    return found == 0 ? 1 : 0;
}

int Calibrate(string[] a)
{
    if (a.Length == 0 || !Enum.TryParse<Fusion2Header>(a[0], true, out var header))
    {
        Console.WriteLine("Usage: rgbblaster calibrate D_LED1");
        return 2;
    }
    const int probe = 64;
    var cfg = new Fusion2Config { Headers = [new() { Header = header, Name = "probe", LedCount = probe }] };
    using var dev = Fusion2Device.Discover(cfg).FirstOrDefault();
    if (dev is null) { Console.WriteLine("No device."); return 1; }
    dev.Open();

    var frame = new Rgb[probe];
    Console.WriteLine("Enter = next LED, q = stop. The last index that lights is LedCount - 1.");
    for (int i = 0; i < probe; i++)
    {
        Array.Clear(frame);
        frame[i] = new Rgb(255, 0, 0);
        dev.SetZone(0, frame);
        dev.Flush();
        Console.Write($"LED {i} ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() == "q") break;
    }
    dev.Release();
    return 0;
}

int BoardFans()
{
    int lines = 0;
    foreach (var line in BoardFanProbe.Report()) { Console.WriteLine(line); lines++; }
    if (lines == 0) Console.WriteLine("No motherboard sensors found. Install PawnIO and run as administrator.");
    return 0;
}

int BoardFanTest(string[] a)
{
    if (!int.TryParse(a.FirstOrDefault(), out int ch) || ch < 1 || ch > 5)
    {
        Console.WriteLine("Usage: board-fan-test <channel 1-5> [duty% ...]. Channel 0 (CPU_FAN) is refused.");
        return 2;
    }
    var steps = a.Skip(1).Select(x => byte.TryParse(x.TrimEnd('%'), out var d) ? Math.Min(d, (byte)100) : (byte)255).ToArray();
    if (steps.Contains((byte)255)) { Console.WriteLine("Duties are 0-100."); return 2; }
    if (steps.Length == 0) steps = [100, 40];
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
    using var hm = new HardwareMonitorSession();
    using var dev = hm.OpenBoardFans([new BoardFanConfig { Channel = ch, MinDutyPercent = Math.Min((byte)30, steps.Min()) }]);
    if (dev is null) { Console.WriteLine("No Super I/O chip. Install PawnIO and run as administrator."); return 1; }
    var fan = dev.Fans[0];
    Console.WriteLine($"{dev.Name} channel {ch}. Ctrl+C restores the BIOS curve.");
    foreach (byte duty in steps)
    {
        fan.SetDutyPercent(duty);
        for (int i = 0; i < 8 && !stop.IsSet; i++)
        {
            Console.WriteLine($"  set {duty,3}%  chip {dev.ReadDuty(0)?.ToString("F0") ?? "-",3}%  {fan.ReadRpm()?.ToString() ?? "-"} RPM");
            stop.Wait(1000);
        }
        if (stop.IsSet) break;
    }
    dev.Release();
    Thread.Sleep(1500);
    Console.WriteLine($"  BIOS  {fan.ReadRpm()?.ToString() ?? "-"} RPM (restored)");
    return 0;
}

int Temps()
{
    using var hm = new HardwareMonitorSession();
    foreach (var (id, hw, name, v) in hm.Temperatures())
        Console.WriteLine($"{(v is float f ? $"{f,5:F1} C" : "    -  ")}  {hw} / {name}  [{id}]");
    foreach (var s in new[] { "cpu", "gpu" })
        Console.WriteLine($"\"{s}\" reads {(hm.ReadCelsius(s) is float t ? $"{t:F1} C" : "nothing")}");
    return 0;
}

LinkLcdDevice? OpenLcd()
{
    try { return LinkLcdDevice.Discover().FirstOrDefault(); }
    catch (Exception ex) { Console.WriteLine($"LCD: open failed: {ex.Message}"); return null; }
}

int LcdTest(string[] a)
{
    using var lcd = OpenLcd();
    if (lcd is null) { Console.WriteLine("No iCUE LINK LCD (1B1C:0C4E)."); return 1; }
    using var hm = new HardwareMonitorSession();
    using var dash = new LcdDashboard();
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
    string? image = a.FirstOrDefault();
    Console.WriteLine($"LCD {lcd.Serial.Length} char serial. {(image is null ? "Status screen" : image)} for 20 s; Ctrl+C stops.");
    var frameFromFile = image is null ? null : dash.FromFile(image);
    for (int i = 0; i < 20 && !stop.IsSet; i++)
    {
        var frame = frameFromFile ?? dash.Render(hm.ReadCelsius("cpu"), hm.ReadCelsius("gpu"), null);
        lcd.ShowJpeg(frame);
        Console.WriteLine($"  frame {i + 1}: {frame.Length} bytes");
        stop.Wait(1000);
    }
    lcd.Release();
    Console.WriteLine("Handed the screen back.");
    return 0;
}

int GpuFanTest(string[] a)
{
    byte duty = a.Length > 0 && byte.TryParse(a[0].TrimEnd('%'), out var d) ? Math.Min(d, (byte)100) : (byte)60;
    int seconds = a.Length > 1 && int.TryParse(a[1], out var sec) ? Math.Clamp(sec, 1, 120) : 20;
    using var hm = new HardwareMonitorSession();
    var (controls, fans) = hm.GpuFans();
    if (controls.Count == 0) { Console.WriteLine("No NVIDIA fan control found."); return 1; }
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
    string Rpm() { var (_, f) = hm.GpuFans(); return string.Join(" ", f.Select(x => $"{x.Name}:{x.Value?.ToString("F0") ?? "-"}")); }
    Console.WriteLine($"{controls.Count} GPU fan control(s). Before: {Rpm()}");
    Console.WriteLine($"Setting {duty}% for {seconds} s; Ctrl+C stops.");
    try
    {
        foreach (var c in controls) c.SetSoftware(duty);
        for (int i = 0; i < seconds && !stop.IsSet; i++)
        {
            stop.Wait(1000);
            Console.WriteLine($"  {i + 1,3}s  {Rpm()}");
        }
    }
    finally
    {
        foreach (var c in controls) c.SetDefault();
        Console.WriteLine("Fans handed back to the card's own curve.");
    }
    return 0;
}

int GpuRingTest(string[] a)
{
    var duties = a.Length > 0
        ? a.Select(x => byte.TryParse(x.TrimEnd('%'), out var d) ? Math.Min(d, (byte)100) : (byte)255).Where(d => d <= 100).ToArray()
        : new byte[] { 0, 30, 40, 50, 60, 80, 100 };
    using var gpu = GigabyteGpuDevice.Discover().FirstOrDefault();
    if (gpu is null) { Console.WriteLine("No Gigabyte GPU found."); return 1; }
    using var hm = new HardwareMonitorSession();
    var (controls, _) = hm.GpuFans();
    if (controls.Count == 0) { Console.WriteLine("No NVIDIA fan control found."); return 1; }
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
    string Rpm() => string.Join("/", hm.GpuFans().Fans.Select(f => f.Value?.ToString("F0") ?? "-"));
    var white = new Rgb(255, 255, 255);
    Console.WriteLine("Each step: fans at the duty, GPU light 1 s ON, 1 s OFF, 3 times. Watch the fan rings. Ctrl+C stops.");
    try
    {
        foreach (var duty in duties)
        {
            foreach (var c in controls) c.SetSoftware(duty);
            Console.WriteLine($"duty {duty}%: settling 4 s");
            if (stop.Wait(4000)) break;
            for (int i = 0; i < 3 && !stop.IsSet; i++)
            {
                gpu.ProbeZoneOnly(GigabyteGpuProtocol.RegColor, 0, white);
                Console.WriteLine($"  duty {duty,3}%  rpm {Rpm(),-14}  light ON");
                if (stop.Wait(1000)) break;
                gpu.ProbeZoneOnly(GigabyteGpuProtocol.RegColor, 0, Rgb.Black);
                Console.WriteLine($"  duty {duty,3}%  rpm {Rpm(),-14}  light OFF");
                if (stop.Wait(1000)) break;
            }
            if (stop.IsSet) break;
        }
    }
    finally
    {
        foreach (var c in controls) c.SetDefault();
        gpu.ProbeZoneOnly(GigabyteGpuProtocol.RegColor, 0, white);
        Console.WriteLine("GPU fans handed back to the card's own curve; GPU light left white.");
    }
    return 0;
}
