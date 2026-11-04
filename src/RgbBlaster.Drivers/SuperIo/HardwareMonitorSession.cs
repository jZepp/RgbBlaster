using LibreHardwareMonitor.Hardware;
using RgbBlaster.Core;

namespace RgbBlaster.Drivers.SuperIo;

/// <summary>
/// One LibreHardwareMonitorLib instance for temperatures and motherboard fan headers. Needs PawnIO and an elevated
/// process for the Super I/O and CPU sensors. Not thread-safe: use it from one thread.
/// </summary>
public sealed class HardwareMonitorSession : ITemperatureSource, IDisposable
{
    private readonly Computer _computer;
    private BoardFanDevice? _boardFans;

    public HardwareMonitorSession()
    {
        _computer = new Computer { IsMotherboardEnabled = true, IsCpuEnabled = true, IsGpuEnabled = true };
        _computer.Open();
    }

    public IHardware? SuperIo =>
        _computer.Hardware.SelectMany(h => h.SubHardware).FirstOrDefault(h => h.HardwareType == HardwareType.SuperIO);

    /// <summary>Opens the configured motherboard headers. Null when no Super I/O chip is reachable or none is configured.</summary>
    public BoardFanDevice? OpenBoardFans(IReadOnlyList<BoardFanConfig> config)
    {
        if (config.Count == 0 || SuperIo is not { } chip) return null;
        return _boardFans = new BoardFanDevice(chip, config);
    }

    /// <summary>
    /// Sensor names: "cpu" (AMD Tctl/Tdie or Intel package), "gpu" (discrete GPU core), "board:&lt;sensor name&gt;" (Super I/O),
    /// or a LibreHardwareMonitor sensor identifier such as "/lpc/it8696e/0/temperature/2".
    /// </summary>
    public float? ReadCelsius(string sensor)
    {
        var s = Find(sensor);
        if (s is null) return null;
        s.Hardware.Update();
        // Some sensors report 0 when access is missing (for example CPU Tctl without admin rights); treat that as no reading.
        return s.Value is float v && v > 0 ? v : null;
    }

    /// <summary>Every temperature sensor with its identifier, for choosing curve sensors.</summary>
    public IEnumerable<(string Id, string Hardware, string Name, float? Value)> Temperatures()
    {
        foreach (var hw in AllHardware())
        {
            hw.Update();
            foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                yield return (s.Identifier.ToString(), hw.Name, s.Name, s.Value);
        }
    }

    /// <summary>Discrete GPU fan controls and their speed sensors. Set with SetSoftware; return to the card's curve with SetDefault.</summary>
    public (IReadOnlyList<IControl> Controls, IReadOnlyList<ISensor> Fans) GpuFans()
    {
        var gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia);
        if (gpu is null) return ([], []);
        gpu.Update();
        var controls = gpu.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control is not null).Select(s => s.Control).ToList();
        var fans = gpu.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
        return (controls, fans);
    }

    private ISensor? Find(string name)
    {
        var temps = AllHardware().SelectMany(h => h.Sensors).Where(s => s.SensorType == SensorType.Temperature).ToList();
        if (name.Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            var cpu = temps.Where(s => s.Hardware.HardwareType == HardwareType.Cpu).ToList();
            return cpu.FirstOrDefault(s => s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                ?? cpu.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                ?? cpu.FirstOrDefault();
        }
        if (name.Equals("gpu", StringComparison.OrdinalIgnoreCase))
        {
            // Discrete NVIDIA first: an AMD CPU's integrated Radeon also shows up as a GPU.
            var gpu = temps.Where(s => s.Hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                .OrderBy(s => s.Hardware.HardwareType == HardwareType.GpuNvidia ? 0 : 1).ToList();
            return gpu.FirstOrDefault(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) ?? gpu.FirstOrDefault();
        }
        if (name.StartsWith("board:", StringComparison.OrdinalIgnoreCase))
        {
            string n = name["board:".Length..];
            return temps.FirstOrDefault(s => s.Hardware.HardwareType == HardwareType.SuperIO && s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        return temps.FirstOrDefault(s => s.Identifier.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<IHardware> AllHardware() =>
        _computer.Hardware.SelectMany(h => h.SubHardware.Prepend(h));

    public void Dispose()
    {
        _boardFans?.Release();
        _computer.Close();
    }
}
