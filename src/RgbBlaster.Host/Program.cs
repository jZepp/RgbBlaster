using RgbBlaster.Core;
using RgbBlaster.Drivers.Fusion2;
using RgbBlaster.Drivers.EVision;
using RgbBlaster.Drivers.Gpu;
using RgbBlaster.Drivers.Logitech;
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
    case "board-fan-test": return BoardFanTest(args.Skip(1).ToArray());
    default:
        Console.WriteLine("""
            rgbblaster identify lights|fans|walk Map zones and fan channels to physical parts
            rgbblaster info                      Show detected Fusion 2 controllers (reads only)
            rgbblaster board-fans                Super I/O fan RPM, PWM and temperatures (reads only; needs PawnIO, run as admin)
            rgbblaster board-fan-test <channel>  Runs one header at 100% then 40%, then hands it back to the BIOS (admin)
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

    using (var loop = new RenderLoop(config, devices, effect))
    {
        loop.Start();
        Console.WriteLine($"Running '{effect.Name}' at {config.FrameRate} fps on {devices.Count} device(s). Ctrl+C to stop.");
        while (!done.Wait(1000))
        {
            foreach (var h in hubs)
            {
                try { h.Poll(); }
                catch (Exception ex) { Console.WriteLine($"LINK poll: {ex.Message}"); }
            }
            var rpm = string.Join(" ", hubs.SelectMany(h => h.Fans).Select(f => $"{f.Id}:{f.ReadRpm()?.ToString() ?? "-"}"));
            Console.WriteLine($"render {loop.LastRenderMs:F2} ms  {rpm}");
        }
    }

    ExitLightingRunner.Apply(devices, config.OnExit);
    return 0;
}

List<IRgbDevice> OpenAll()
{
    var list = new List<IRgbDevice>();
    foreach (var d in Fusion2Device.Discover(config.Fusion2).Cast<IRgbDevice>().Concat(LinkHubDevice.Discover()).Concat(GigabyteGpuDevice.Discover()).Concat(LogitechMouseDevice.Discover()).Concat(EVisionKeyboardDevice.Discover()))
    {
        try { d.Open(); list.Add(d); }
        catch (Exception ex) { Console.WriteLine($"{d.Name}: open failed: {ex.Message}"); d.Dispose(); }
    }
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
        Console.WriteLine("Usage: board-fan-test <channel 1-5>. Channel 0 (CPU_FAN) is refused.");
        return 2;
    }
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
    using var dev = BoardFanDevice.Open([new BoardFanConfig { Channel = ch, MinDutyPercent = 30 }]);
    if (dev is null) { Console.WriteLine("No Super I/O chip. Install PawnIO and run as administrator."); return 1; }
    var fan = dev.Fans[0];
    Console.WriteLine($"{dev.Name} channel {ch}. Ctrl+C restores the BIOS curve.");
    foreach (byte duty in new byte[] { 100, 40 })
    {
        fan.SetDutyPercent(duty);
        for (int i = 0; i < 8 && !stop.IsSet; i++)
        {
            Console.WriteLine($"  {duty,3}%  {fan.ReadRpm()?.ToString() ?? "-"} RPM");
            stop.Wait(1000);
        }
        if (stop.IsSet) break;
    }
    dev.Release();
    Thread.Sleep(1500);
    Console.WriteLine($"  BIOS  {fan.ReadRpm()?.ToString() ?? "-"} RPM (restored)");
    return 0;
}
