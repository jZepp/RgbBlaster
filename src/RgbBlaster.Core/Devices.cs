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

/// <summary>
/// Firmware stand-ins for effects. Rainbow and Breathe match the effect. Cycle (one hue turning through the wheel) and
/// Wave (a band of Color moving) and Sparkle (lights that flash and fade) are the nearest firmware modes for effects a device cannot stream.
/// Firmware runs a device-specific mode chosen by the user (<see cref="NativeEffect.Mode"/>).
/// </summary>
public enum NativeEffectKind { None, Rainbow, Breathe, Cycle, Wave, Sparkle, Firmware }

/// <summary>An effect described as parameters, for devices that can run it in firmware instead of taking frames.</summary>
/// <param name="Mode">Firmware only: the device's mode number.</param>
/// <param name="Speed">Firmware only: the device's speed step.</param>
/// <param name="Random">The device picks the colors (the effect uses several).</param>
public readonly record struct NativeEffect(NativeEffectKind Kind, Rgb Color = default, float PeriodSeconds = 0,
    byte Mode = 0, byte Speed = 0, bool Random = false)
{
    public static NativeEffect None => default;
}

/// <summary>
/// A device whose frame updates are costly (for example, stored writes) and that can run some effects in firmware.
/// While a native effect is active, the device ignores frames.
/// </summary>
public interface INativeEffectDevice
{
    /// <summary>Called when the running effect changes. Takes effect on the next Flush.</summary>
    void SetNativeEffect(NativeEffect effect);
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

public interface ITemperatureSource
{
    /// <summary>Temperature in °C, or null when the sensor is missing or has no reading.</summary>
    float? ReadCelsius(string sensor);
}

public interface IFanDevice
{
    IReadOnlyList<IFanChannel> Fans { get; }
}
