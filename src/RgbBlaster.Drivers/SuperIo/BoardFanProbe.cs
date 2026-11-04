using LibreHardwareMonitor.Hardware;

namespace RgbBlaster.Drivers.SuperIo;

/// <summary>
/// Read-only report of motherboard Super I/O chips and their fan, control and temperature sensors, through
/// LibreHardwareMonitorLib. Port access needs the PawnIO driver and an elevated process; without them no chip is found.
/// Opening the library does not change fan control.
/// </summary>
public static class BoardFanProbe
{
    public static IEnumerable<string> Report()
    {
        var computer = new Computer { IsMotherboardEnabled = true };
        computer.Open();
        try
        {
            foreach (var board in computer.Hardware)
            {
                board.Update();
                yield return $"{board.HardwareType}: {board.Name}";
                foreach (var chip in board.SubHardware)
                {
                    chip.Update();
                    yield return $"  {chip.HardwareType}: {chip.Name}";
                    foreach (var s in chip.Sensors
                                 .Where(s => s.SensorType is SensorType.Fan or SensorType.Control or SensorType.Temperature)
                                 .OrderBy(s => s.SensorType).ThenBy(s => s.Index))
                    {
                        string unit = s.SensorType switch { SensorType.Fan => " RPM", SensorType.Control => " %", _ => " C" };
                        string v = s.Value is float f ? $"{f:F0}{unit}" : "-";
                        yield return $"    {s.SensorType,-11} {s.Index,2} {s.Name,-24} {v}";
                    }
                }
            }
        }
        finally
        {
            computer.Close();
        }
    }
}
