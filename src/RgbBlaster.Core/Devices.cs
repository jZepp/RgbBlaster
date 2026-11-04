namespace RgbBlaster.Core;

public enum DeviceKind { Motherboard, FanHub, Cooler, Mouse, Keyboard, Gpu, Lcd, Accessory }

/// <param name="Id">Stable within the device; used by layout files.</param>
/// <param name="Positions">Optional LED positions inside the zone, 0..1 each axis, used by the Grid placement.</param>
public sealed record LedZoneInfo(string Id, string Name, int LedCount, IReadOnlyList<(float X, float Y)>? Positions = null);

/// <summary>
/// A device whose LEDs the engine drives.
/// Threading contract: <see cref="SetZone"/> is called by the render thread,
/// <see cref="Flush"/> by the device's own output worker. Implementations synchronise between the two.
/// </summary>
public interface IRgbDevice : IDisposable
{
    string Id { get; }
    string Name { get; }
    DeviceKind Kind { get; }
    IReadOnlyList<LedZoneInfo> Zones { get; }

    /// <summary>Takes software control of the lighting.</summary>
    void Open();

    /// <summary>Stages colors for a zone. Must not block on I/O.</summary>
    void SetZone(int zoneIndex, ReadOnlySpan<Rgb> colors);

    /// <summary>Sends staged changes to the hardware. Implementations skip unchanged data.</summary>
    void Flush();

    /// <summary>Returns the device to its firmware-controlled mode.</summary>
    void Release();
}

/// <summary>A device that can store colors as its own power-on lighting, used when the app is not running.</summary>
public interface IPersistentLighting
{
    bool CanSaveLighting { get; }

    /// <summary>Stores the last flushed colors as the device's own lighting, then returns control to its firmware.</summary>
    void SaveLightingAndRelease();
}

public interface IFanChannel
{
    string Id { get; }
    string Name { get; }
    /// <summary>Pump channels have a duty floor and must never stop.</summary>
    bool IsPump { get; }
    int? ReadRpm();
    void SetDutyPercent(byte percent);
}

public interface IFanDevice
{
    IReadOnlyList<IFanChannel> Fans { get; }
}
