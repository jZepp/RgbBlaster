namespace RgbBlaster.Core;

/// <summary>
/// Vendor RGB apps that write to the same devices as RgbBlaster. Two writers on one device make the lights flicker,
/// and on 2026-11-04 the pump head and LINK hub dropped off USB while the Redragon app ran next to the service.
/// </summary>
public static class VendorApps
{
    /// <param name="Processes">Process names without ".exe", compared without case.</param>
    public sealed record App(string Name, string Devices, string[] Processes);

    // Names checked on this PC: Redragon, iCUE. The others are the vendors' usual process names.
    public static readonly App[] Known =
    [
        new("Corsair iCUE", "LINK hub, pump LCD", ["iCUE", "iCUEDevicePluginHost", "CorsairDeviceControlService", "Corsair Device Control Service"]),
        new("Redragon K580 software", "keyboard", ["Redragon K580RGB"]),
        new("Logitech G HUB", "mouse", ["lghub", "lghub_agent", "lghub_system_tray"]),
        new("GIGABYTE Control Center", "motherboard, GPU", ["GCC"]),
        new("GIGABYTE RGB Fusion", "motherboard, GPU", ["RGBFusion"]),
        new("SignalRGB", "all lighting", ["SignalRgb", "SignalRgbLauncher"]),
        new("OpenRGB", "all lighting", ["OpenRGB"]),
        new("OpenLinkHub", "LINK hub, pump LCD", ["OpenLinkHub"]),
        new("FanControl", "LINK hub (with the Corsair plugin)", ["FanControl"]),
    ];

    /// <summary>The known apps with at least one running process, in table order.</summary>
    public static List<App> Find(IEnumerable<string> runningProcesses)
    {
        var running = new HashSet<string>(runningProcesses, StringComparer.OrdinalIgnoreCase);
        return Known.Where(a => a.Processes.Any(running.Contains)).ToList();
    }

    /// <summary>The known apps running now.</summary>
    public static List<App> Running()
    {
        var names = new List<string>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
            using (p) names.Add(p.ProcessName);
        return Find(names);
    }
}
