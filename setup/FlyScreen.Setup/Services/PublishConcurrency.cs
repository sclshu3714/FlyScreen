using System.Runtime.InteropServices;

namespace FlyScreen.Setup.Services;

/// <summary>
/// 按本机 CPU / 可用内存估算 Setup 并行打包上限。
/// 单个 Release publish（尤其 Maui / Installer）吃内存重，不能按逻辑核全开。
/// </summary>
public static class PublishConcurrency
{
    /// <summary>每个并行任务预估占用的可用内存（GB）。</summary>
    public const double GigabytesPerJob = 6.0;

    /// <summary>硬上限，避免过多 WinAppSDK/WPF 编译互相拖垮。</summary>
    public const int HardCap = 4;

    public sealed record Decision(
        int MaxJobs,
        int LogicalCpus,
        double AvailableMemoryGb,
        int ByCpu,
        int ByMemory,
        string Summary);

    public static Decision Resolve()
    {
        var cpus = Math.Max(1, Environment.ProcessorCount);
        // 外层并行只开一半逻辑核：dotnet publish 内部已多核
        var byCpu = Math.Max(1, cpus / 2);

        var availGb = TryGetAvailablePhysicalMemoryGb()
                      ?? TryGetGcAvailableMemoryGb()
                      ?? 8.0;
        var byMemory = Math.Max(1, (int)Math.Floor(availGb / GigabytesPerJob));

        var max = Math.Clamp(Math.Min(byCpu, byMemory), 1, HardCap);
        var summary =
            $"并行上限 {max}（逻辑核 {cpus}→按CPU {byCpu}；可用内存约 {availGb:0.#} GB→按内存 {byMemory}；硬上限 {HardCap}；每任务约 {GigabytesPerJob:0.#} GB）";

        return new Decision(max, cpus, availGb, byCpu, byMemory, summary);
    }

    private static double? TryGetGcAvailableMemoryGb()
    {
        try
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (bytes <= 0) return null;
            return bytes / (1024d * 1024d * 1024d);
        }
        catch
        {
            return null;
        }
    }

    private static double? TryGetAvailablePhysicalMemoryGb()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) return null;
            if (status.AvailPhys == 0) return null;
            return status.AvailPhys / (1024d * 1024d * 1024d);
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
