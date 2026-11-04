using RgbBlaster.Core;
using RgbBlaster.Drivers.Fusion2;
using RgbBlaster.Drivers.EVision;
using RgbBlaster.Drivers.Gpu;
using RgbBlaster.Drivers.Logitech;
using RgbBlaster.Drivers.Link;
using RgbBlaster.Drivers.SuperIo;

/// <summary>Interactive mapping of software zones and fan channels to physical parts.</summary>
internal static class Identify
{
    private const int ProbeLeds = 64;
    private const byte OtherFanPercent = 30;

    public static int Run(string[] a, AppConfig config)
    {
        var flags = a.Where(x => x.StartsWith("--")).ToArray();
        a = a.Where(x => !x.StartsWith("--")).ToArray();
        var mode = (a.ElementAtOrDefault(0) ?? "lights").ToLowerInvariant();

        // Drive every Fusion 2 header, configured or not, so unconfigured wiring shows up too.
        var probe = new Fusion2Config
        {
            Headers = Enum.GetValues<Fusion2Header>().Select(h => new Fusion2HeaderConfig
            {
                Header = h,
                Name = config.Fusion2.Headers.FirstOrDefault(c => c.Header == h)?.Name is { Length: > 0 } n ? n : h.ToString(),
                LedCount = ProbeLeds,
                ColorOrder = config.Fusion2.Headers.FirstOrDefault(c => c.Header == h)?.ColorOrder ?? ColorOrderSetting.Auto,
            }).ToList(),
            // Experiments, off by default: --all-zones drives firmware zones 0-10 (walk index = zone number);
            // --lamp-off sends 0x48 00 although the board does not report support for it.
            OnboardZones = flags.Contains("--all-zones") ? [.. Enumerable.Range(0, 11)] : [],
            ForceLampArrayOff = flags.Contains("--lamp-off"),
        };

        var devices = new List<IRgbDevice>();
        foreach (var d in Fusion2Device.Discover(probe).Cast<IRgbDevice>().Concat(LinkHubDevice.Discover()).Concat(GigabyteGpuDevice.Discover()).Concat(LogitechMouseDevice.Discover()).Concat(EVisionKeyboardDevice.Discover()))
        {
            try { d.Open(); devices.Add(d); }
            catch (Exception ex) { Console.WriteLine($"{d.Name}: open failed: {ex.Message}"); d.Dispose(); }
        }
        if (devices.Count == 0) { Console.WriteLine("No devices."); return 1; }

        // The LINK hub and the board return to their own lighting when they get no frames, so keep them fed while we wait for input.
        var hubs = devices.OfType<LinkHubDevice>().ToList();
        using var stop = new CancellationTokenSource();
        var keepAlive = new Thread(() =>
        {
            while (!stop.Token.WaitHandle.WaitOne(500))
                foreach (var d in devices)
                {
                    try { d.Flush(); if (d is LinkHubDevice h) h.Poll(); }
                    catch (Exception ex) { Console.WriteLine($"{d.Id} keep-alive: {ex.Message}"); }
                }
        }) { IsBackground = true, Name = "keepalive" };
        keepAlive.Start();

        try
        {
            return mode switch
            {
                "lights" => Lights(devices, Rgb.Parse(a.ElementAtOrDefault(1) ?? "FFFFFF")),
                "fans" => Fans(devices.OfType<LinkHubDevice>().ToList()),
                "walk" => Walk(devices, a.ElementAtOrDefault(1), a.ElementAtOrDefault(2), a.ElementAtOrDefault(3)),
                "gpu" => GpuZones(devices.OfType<GigabyteGpuDevice>().FirstOrDefault(), a.ElementAtOrDefault(1), a.ElementAtOrDefault(2) == "single"),
                _ => Usage(),
            };
        }
        finally
        {
            stop.Cancel();
            keepAlive.Join(2000);
            ExitLightingRunner.Apply(devices, config.OnExit);
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            rgbblaster identify lights [RRGGBB]      Light chosen zones (default white)
            rgbblaster identify fans                 Run one LINK channel at 100%, others at 30%
            rgbblaster identify walk <device> <zone> [i,j,..] Light one LED at a time in a zone (e.g. walk link ch1)
            rgbblaster identify gpu [spin%] [single] Light each GPU hardware zone, live-color then stored-effect register; spin% holds the GPU fans at that duty during the walk (admin); single writes only the zone under test
            Flags: --all-zones (board firmware zones 0-10), --lamp-off (force lamp-array mode off)
            """);
        return 2;
    }

    /// <summary>Menu: lights the chosen zone(s) in one color with every other zone off.</summary>
    private static int Lights(List<IRgbDevice> devices, Rgb color)
    {
        var targets = devices.SelectMany(d => d.Zones.Select((z, i) => (Device: d, Index: i, Zone: z))).ToList();
        var on = new HashSet<int>();
        while (true)
        {
            Console.WriteLine();
            for (int t = 0; t < targets.Count; t++)
            {
                var (dev, _, zone) = targets[t];
                Console.WriteLine($" {(on.Contains(t) ? "*" : " ")} {t + 1,2}  {dev.Id}/{zone.Id,-9} {zone.Name}  ({zone.LedCount} LEDs)");
            }
            Console.Write("Zone number(s) to light (e.g. 3 or 3,10), a = all, o = off, q = quit > ");
            var input = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (input is null or "q") return 0;

            if (input == "a") on = Enumerable.Range(0, targets.Count).ToHashSet();
            else if (input == "o") on.Clear();
            else
            {
                var picked = input.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, out var n) ? n - 1 : -1)
                    .ToList();
                if (picked.Count == 0 || picked.Any(n => n < 0 || n >= targets.Count))
                {
                    Console.WriteLine($"Enter numbers from 1 to {targets.Count}.");
                    continue;
                }
                on = picked.ToHashSet();
            }

            foreach (var d in devices)
            {
                for (int z = 0; z < d.Zones.Count; z++)
                {
                    int t = targets.FindIndex(x => x.Device == d && x.Index == z);
                    d.SetZone(z, Enumerable.Repeat(on.Contains(t) ? color : Rgb.Black, d.Zones[z].LedCount).ToArray());
                }
                d.Flush();
            }
        }
    }

    /// <summary>
    /// Runs one channel at 100% and all other fans at 30%; pumps that are not the target stay at 100%.
    /// When the target is the AIO, every LINK fan channel is at 30%: fans still running fast are driven by the AIO channel.
    /// </summary>
    private static int Fans(List<LinkHubDevice> hubs)
    {
        if (hubs.Count == 0) { Console.WriteLine("No LINK hub."); return 1; }
        var fans = hubs.SelectMany(h => h.Fans.Select(f => (Hub: h, Fan: f))).ToList();
        Console.WriteLine("Each step holds until you press Enter (q = quit). Watch and listen for the fast fan(s).");

        foreach (var (hub, target) in fans)
        {
            foreach (var (_, f) in fans)
                f.SetDutyPercent(f == target || f.IsPump ? (byte)100 : OtherFanPercent);
            Console.WriteLine($"-> {target.Name} at 100%");
            while (true)
            {
                foreach (var h in hubs) h.Poll();
                Console.Write("\r   " + string.Join("  ", fans.Select(x => $"{x.Fan.Id}:{x.Fan.ReadRpm()?.ToString() ?? "-",5}")) + "   ");
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    Console.WriteLine();
                    if (k.KeyChar is 'q' or 'Q') return Restore(fans);
                    break;
                }
                Thread.Sleep(1000);
            }
        }
        return Restore(fans);
    }

    private static int Restore(List<(LinkHubDevice Hub, IFanChannel Fan)> fans)
    {
        foreach (var (_, f) in fans)
            f.SetDutyPercent(f.IsPump ? LinkHubDevice.DefaultPumpPercent : LinkHubDevice.DefaultFanPercent);
        foreach (var h in fans.Select(x => x.Hub).Distinct()) h.Poll();
        return 0;
    }

    /// <summary>Steps through GPU hardware zones 0-7 with the live-color register (0x16), then the stored-effect register (0x12).</summary>
    private static int GpuZones(GigabyteGpuDevice? gpu, string? spin, bool single)
    {
        if (gpu is null) { Console.WriteLine("No Gigabyte GPU found."); return 1; }
        if (spin is null) return GpuZoneSteps(gpu, null, single);
        byte duty = byte.TryParse(spin.TrimEnd('%'), out var d) ? Math.Min(d, (byte)100) : (byte)60;
        using var hm = new HardwareMonitorSession();
        var (controls, _) = hm.GpuFans();
        Console.WriteLine($"Holding {controls.Count} GPU fan control(s) at {duty}%.");
        try
        {
            foreach (var c in controls) c.SetSoftware(duty);
            return GpuZoneSteps(gpu, hm, single);
        }
        finally
        {
            foreach (var c in controls) c.SetDefault();
            Console.WriteLine("GPU fans handed back to the card's own curve.");
        }
    }

    private static int GpuZoneSteps(GigabyteGpuDevice gpu, HardwareMonitorSession? hm, bool single)
    {
        Console.WriteLine("Each step lights one hardware zone red. Note what lights. Enter = next, q = quit.");
        foreach (var (reg, label) in new[] { (GigabyteGpuProtocol.RegColor, "live 0x16"), (GigabyteGpuProtocol.RegMode, "effect 0x12") })
            for (int hz = 0; hz < 8; hz++)
            {
                if (!single) gpu.ProbeZone(reg, hz);
                else
                {
                    if (hz > 0) gpu.ProbeZoneOnly(reg, hz - 1, Rgb.Black);
                    gpu.ProbeZoneOnly(reg, hz, new Rgb(255, 0, 0));
                }
                string rpm = hm is null ? "" : "  rpm " + string.Join("/", hm.GpuFans().Fans.Select(f => f.Value?.ToString("F0") ?? "-"));
                Console.Write($"{label} zone {hz}{rpm} > ");
                if (Console.ReadLine()?.Trim().ToLowerInvariant() == "q") return 0;
            }
        return 0;
    }

    /// <summary>Lights LED 0..n-1 of one zone in turn, others off. Confirms LED order and count.</summary>
    /// <param name="only">Optional comma-separated LED indices to walk instead of every LED.</param>
    private static int Walk(List<IRgbDevice> devices, string? deviceId, string? zoneId, string? only = null)
    {
        var dev = devices.FirstOrDefault(d => d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
        int zi = dev?.Zones.ToList().FindIndex(z => z.Id.Equals(zoneId, StringComparison.OrdinalIgnoreCase)) ?? -1;
        if (dev is null || zi < 0)
        {
            Console.WriteLine("Zones: " + string.Join(", ", devices.SelectMany(d => d.Zones.Select(z => $"{d.Id} {z.Id}"))));
            return 2;
        }
        foreach (var d in devices)
        {
            for (int z = 0; z < d.Zones.Count; z++) d.SetZone(z, new Rgb[d.Zones[z].LedCount]);
            d.Flush();
        }
        var frame = new Rgb[dev.Zones[zi].LedCount];
        var order = only is null
            ? Enumerable.Range(0, frame.Length).ToList()
            : only.Split(',').Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n >= 0 && n < frame.Length).ToList();
        Console.WriteLine($"{dev.Id}/{dev.Zones[zi].Id}: {frame.Length} LEDs, walking {order.Count}. Enter = next, q = quit.");
        foreach (int i in order)
        {
            Array.Clear(frame);
            frame[i] = new Rgb(255, 0, 0);
            dev.SetZone(zi, frame);
            dev.Flush();
            Console.Write($"LED {i} > ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() == "q") break;
        }
        return 0;
    }
}
