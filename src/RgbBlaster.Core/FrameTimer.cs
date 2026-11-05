using System.Runtime.InteropServices;

namespace RgbBlaster.Core;

/// <summary>
/// Sleeps with about 1 ms accuracy. Thread.Sleep follows the system timer, which is often 15.6 ms on Windows;
/// a high-resolution waitable timer does not. Falls back to Thread.Sleep where that timer is unavailable.
/// </summary>
public sealed partial class FrameTimer : IDisposable
{
    private readonly IntPtr _handle;

    public FrameTimer()
    {
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS. Windows 10 1803 and later.
        if (OperatingSystem.IsWindows()) _handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003);
    }

    public void Wait(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        if (_handle != IntPtr.Zero)
        {
            long due = -delay.Ticks; // negative: relative time in 100 ns units
            if (SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_handle, 0xFFFFFFFF);
                return;
            }
        }
        Thread.Sleep(delay);
    }

    public void Dispose() { if (_handle != IntPtr.Zero) CloseHandle(_handle); }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
