using LibreHardwareMonitor.Hardware;
using RgbBlaster.Core;

namespace RgbBlaster.Drivers.SuperIo;

/// <summary>
/// Motherboard fan headers on the Super I/O chip, through LibreHardwareMonitorLib (needs PawnIO and an elevated process).
/// Only headers listed in config are exposed. Each set duty is raised to the header's minimum, so a header never stalls.
/// <see cref="Release"/> and <see cref="Dispose"/> hand every header back to the BIOS curve. If the process dies without
/// them, a header keeps its last duty until reboot.
/// </summary>
public sealed class BoardFanDevice : IFanDevice, IDisposable
{
    private sealed class Channel(BoardFanDevice owner, BoardFanConfig cfg, ISensor control, ISensor? rpm) : IFanChannel
    {
        public string Id => $"board{cfg.Channel}";
        public string Name => string.IsNullOrEmpty(cfg.Name) ? $"Board fan #{cfg.Channel + 1}" : cfg.Name;
        public bool IsPump => false;
        public byte MinDuty => cfg.MinDutyPercent;
        public bool Overridden { get; private set; }

        public int? ReadRpm()
        {
            owner.Update();
            return rpm?.Value is float f ? (int)f : null;
        }

        /// <summary>PWM duty read back from the chip; null while the BIOS curve controls the header.</summary>
        public float? ReadDuty()
        {
            owner.Update();
            return control.Value;
        }

        public void SetDutyPercent(byte percent)
        {
            byte duty = Math.Clamp(percent, cfg.MinDutyPercent, (byte)100);
            control.Control!.SetSoftware(duty);
            Overridden = true;
        }

        public void Restore()
        {
            if (!Overridden) return;
            control.Control!.SetDefault();
            Overridden = false;
        }
    }

    private readonly Computer _computer;
    private readonly IHardware _chip;
    private readonly List<Channel> _fans = [];

    private BoardFanDevice(Computer computer, IHardware chip, IEnumerable<BoardFanConfig> config)
    {
        _computer = computer;
        _chip = chip;
        chip.Update();
        foreach (var c in config)
        {
            var control = chip.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control && s.Index == c.Channel);
            if (control?.Control is null) throw new InvalidOperationException($"{chip.Name} has no controllable fan channel {c.Channel}.");
            var rpm = chip.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Index == c.Channel);
            _fans.Add(new Channel(this, c, control, rpm));
        }
    }

    public string Name => _chip.Name;
    public IReadOnlyList<IFanChannel> Fans => _fans;

    /// <summary>Opens the first Super I/O chip. Returns null when no chip is reachable or no header is configured.</summary>
    public static BoardFanDevice? Open(IReadOnlyList<BoardFanConfig> config)
    {
        if (config.Count == 0) return null;
        var computer = new Computer { IsMotherboardEnabled = true };
        computer.Open();
        var chip = computer.Hardware.SelectMany(h => h.SubHardware).FirstOrDefault(h => h.HardwareType == HardwareType.SuperIO);
        if (chip is null) { computer.Close(); return null; }
        try { return new BoardFanDevice(computer, chip, config); }
        catch { computer.Close(); throw; }
    }

    private void Update() => _chip.Update();

    /// <summary>Diagnostic: PWM duty of fan <paramref name="index"/> read back from the chip.</summary>
    public float? ReadDuty(int index) => _fans[index].ReadDuty();

    /// <summary>Hands every header this app changed back to the BIOS curve.</summary>
    public void Release()
    {
        foreach (var f in _fans) f.Restore();
    }

    public void Dispose()
    {
        Release();
        _computer.Close();
    }
}
