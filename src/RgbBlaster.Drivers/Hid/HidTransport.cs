using HidSharp;

namespace RgbBlaster.Drivers.Hid;

/// <summary>Byte-level HID access. Buffers include the report ID at index 0.</summary>
public interface IHidTransport : IDisposable
{
    void SetFeature(ReadOnlySpan<byte> report);
    /// <summary>report[0] must hold the report ID on entry.</summary>
    void GetFeature(Span<byte> report);
    void Write(ReadOnlySpan<byte> report);
    int Read(Span<byte> report, int timeoutMs);
}

/// <summary>HidSharp-backed transport. Reuses its arrays so steady-state I/O does not allocate.</summary>
public sealed class HidSharpTransport : IHidTransport
{
    private readonly HidStream _stream;
    private readonly byte[] _feature;
    private readonly byte[] _output;
    private readonly byte[] _input;

    public HidSharpTransport(HidDevice device)
    {
        if (!device.TryOpen(out _stream))
            throw new IOException($"Could not open {device.DevicePath}. Another program probably holds it.");
        _feature = new byte[Math.Max(1, device.GetMaxFeatureReportLength())];
        _output = new byte[Math.Max(1, device.GetMaxOutputReportLength())];
        _input = new byte[Math.Max(1, device.GetMaxInputReportLength())];
    }

    public void SetFeature(ReadOnlySpan<byte> report)
    {
        Stage(report, _feature);
        _stream.SetFeature(_feature);
    }

    public void GetFeature(Span<byte> report)
    {
        Array.Clear(_feature);
        _feature[0] = report[0];
        _stream.GetFeature(_feature);
        _feature.AsSpan(0, Math.Min(report.Length, _feature.Length)).CopyTo(report);
    }

    public void Write(ReadOnlySpan<byte> report)
    {
        Stage(report, _output);
        _stream.Write(_output);
    }

    public int Read(Span<byte> report, int timeoutMs)
    {
        _stream.ReadTimeout = timeoutMs;
        int n = _stream.Read(_input, 0, _input.Length);
        _input.AsSpan(0, Math.Min(n, report.Length)).CopyTo(report);
        return n;
    }

    private static void Stage(ReadOnlySpan<byte> src, byte[] dst)
    {
        if (src.Length > dst.Length)
            throw new ArgumentException($"Report is {src.Length} bytes; device accepts {dst.Length}.");
        src.CopyTo(dst);
        dst.AsSpan(src.Length).Clear();
    }

    public void Dispose() => _stream.Dispose();
}
