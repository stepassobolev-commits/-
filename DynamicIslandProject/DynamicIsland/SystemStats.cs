using System.Runtime.InteropServices;

namespace DynamicIsland;

/// <summary>Загрузка процессора и памяти (без дополнительных библиотек).</summary>
public static class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static long _lastIdle;
    private static long _lastKernel;
    private static long _lastUser;

    public static double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        var idleDelta = idle - _lastIdle;
        var totalDelta = (kernel - _lastKernel) + (user - _lastUser);
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        if (totalDelta <= 0) return 0;
        return Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    public static double MemoryPercent()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
    }
}
