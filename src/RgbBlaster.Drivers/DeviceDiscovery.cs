using RgbBlaster.Core;
using RgbBlaster.Drivers.EVision;
using RgbBlaster.Drivers.Fusion2;
using RgbBlaster.Drivers.Gpu;
using RgbBlaster.Drivers.Link;
using RgbBlaster.Drivers.Logitech;

namespace RgbBlaster.Drivers;

public static class DeviceDiscovery
{
    /// <summary>Finds and opens every supported lighting device. Devices that fail to open are reported and skipped.</summary>
    public static List<IRgbDevice> OpenAll(AppConfig config, Action<string> log)
    {
        var list = new List<IRgbDevice>();
        var found = Fusion2Device.Discover(config.Fusion2).Cast<IRgbDevice>()
            .Concat(LinkHubDevice.Discover())
            .Concat(GigabyteGpuDevice.Discover())
            .Concat(LogitechMouseDevice.Discover())
            .Concat(EVisionKeyboardDevice.Discover());
        foreach (var d in found)
        {
            try { d.Open(); list.Add(d); }
            catch (Exception ex) { log($"{d.Name}: open failed: {ex.Message}"); d.Dispose(); }
        }
        return list;
    }
}
