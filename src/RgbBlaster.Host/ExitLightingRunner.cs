using RgbBlaster.Core;

/// <summary>Leaves the lights in the state <see cref="ExitLighting"/> asks for, then disposes the devices.</summary>
internal static class ExitLightingRunner
{
    public static void Apply(List<IRgbDevice> devices, ExitLighting mode)
    {
        foreach (var d in devices)
        {
            try
            {
                switch (mode)
                {
                    case ExitLighting.Firmware:
                        d.Release();
                        break;
                    case ExitLighting.Off:
                        for (int z = 0; z < d.Zones.Count; z++) d.SetZone(z, new Rgb[d.Zones[z].LedCount]);
                        d.Flush();
                        break;
                    case ExitLighting.SaveLast when d is IPersistentLighting p:
                        p.SaveLightingAndRelease();
                        break;
                    case ExitLighting.SaveLast:
                        // No stored-lighting support: the device holds the last frame until its own timeout.
                        break;
                }
            }
            catch (Exception ex) { Console.WriteLine($"{d.Id}: exit lighting failed: {ex.Message}"); }
            d.Dispose();
        }
        devices.Clear();
    }
}
