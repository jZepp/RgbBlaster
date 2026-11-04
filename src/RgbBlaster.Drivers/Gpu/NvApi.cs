using System.Runtime.InteropServices;

namespace RgbBlaster.Drivers.Gpu;

/// <summary>Raw I2C access to the device bus of one GPU.</summary>
public interface II2cBus
{
    /// <summary>Writes <paramref name="data"/> to the 7-bit address with no register byte.</summary>
    void Write(byte address, ReadOnlySpan<byte> data);

    /// <summary>Reads <paramref name="data"/>.Length bytes from the 7-bit address with no register byte.</summary>
    void Read(byte address, Span<byte> data);
}

/// <summary>One NVIDIA GPU as NVAPI reports it.</summary>
public sealed record NvGpu(IntPtr Handle, ushort Vendor, ushort Device, ushort SubVendor, ushort SubDevice);

/// <summary>
/// Minimal NVAPI binding: GPU enumeration, PCI identifiers and I2C on port 1, where board RGB controllers sit.
/// Functions are resolved through nvapi_QueryInterface by interface ID.
/// </summary>
public static class NvApi
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetPciIdentifiers = 0x2DDFB66E;
    private const uint IdI2cWriteEx = 0x283AC65A;
    private const uint IdI2cReadEx = 0x4D7B0709;
    private const int MaxPhysicalGpus = 64;
    private const byte RgbPort = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct I2cInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DeviceAddress; // 8-bit form: 7-bit address << 1
        public IntPtr RegisterAddress;
        public uint RegisterAddressSize;
        public IntPtr Data;
        public uint Size;
        public uint Speed;         // legacy field; 0xFFFF selects SpeedKhz
        public int SpeedKhz;       // 0 = driver default
        public byte PortId;
        public uint IsPortIdSet;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumGpusFn([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PciIdsFn(IntPtr gpu, out uint deviceId, out uint subSystemId, out uint revisionId, out uint extDeviceId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int I2cFn(IntPtr gpu, ref I2cInfoV3 info, out uint unknown);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    private static readonly Lazy<bool> Loaded = new(Load);
    private static EnumGpusFn? _enum;
    private static PciIdsFn? _pciIds;
    private static I2cFn? _write;
    private static I2cFn? _read;

    private static T? Get<T>(uint id) where T : Delegate
    {
        var p = QueryInterface(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static bool Load()
    {
        try
        {
            var init = Get<InitializeFn>(IdInitialize);
            if (init is null || init() != 0) return false;
            _enum = Get<EnumGpusFn>(IdEnumPhysicalGpus);
            _pciIds = Get<PciIdsFn>(IdGetPciIdentifiers);
            _write = Get<I2cFn>(IdI2cWriteEx);
            _read = Get<I2cFn>(IdI2cReadEx);
            return _enum is not null && _pciIds is not null && _write is not null && _read is not null;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>All physical NVIDIA GPUs; empty when NVAPI is not available.</summary>
    public static IReadOnlyList<NvGpu> EnumerateGpus()
    {
        if (!Loaded.Value) return [];
        var handles = new IntPtr[MaxPhysicalGpus];
        if (_enum!(handles, out int count) != 0) return [];
        var list = new List<NvGpu>();
        for (int i = 0; i < count; i++)
        {
            if (_pciIds!(handles[i], out uint dev, out uint sub, out _, out _) != 0) continue;
            list.Add(new NvGpu(handles[i], (ushort)dev, (ushort)(dev >> 16), (ushort)sub, (ushort)(sub >> 16)));
        }
        return list;
    }

    /// <summary>I2C bus on the RGB port of one GPU. NVAPI status codes surface as <see cref="IOException"/>.</summary>
    public sealed class Bus(IntPtr gpu) : II2cBus
    {
        public void Write(byte address, ReadOnlySpan<byte> data) => Transfer(_write!, address, data.ToArray(), "write");

        public void Read(byte address, Span<byte> data)
        {
            var buf = new byte[data.Length];
            Transfer(_read!, address, buf, "read");
            buf.CopyTo(data);
        }

        private void Transfer(I2cFn fn, byte address, byte[] buf, string what)
        {
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                var info = new I2cInfoV3
                {
                    Version = (3u << 16) | (uint)Marshal.SizeOf<I2cInfoV3>(),
                    DeviceAddress = (byte)(address << 1),
                    Data = pin.AddrOfPinnedObject(),
                    Size = (uint)buf.Length,
                    Speed = 0xFFFF,
                    PortId = RgbPort,
                    IsPortIdSet = 1,
                };
                int status = fn(gpu, ref info, out _);
                if (status != 0) throw new IOException($"NVAPI I2C {what} at 0x{address:X2} failed: status {status}.");
            }
            finally { pin.Free(); }
        }
    }
}
