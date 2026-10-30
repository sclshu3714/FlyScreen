using System.Runtime.InteropServices;
using FlyScreen.Native;

namespace FlyScreen.ScreenExtend;

public readonly record struct MonitorRect(int Left, int Top, int Width, int Height, bool IsPrimary)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

/// <summary>枚举系统监视器（含虚拟 IDD 出现后的扩展屏）。</summary>
public static class DisplayMonitorEnum
{
    public static List<MonitorRect> GetAll()
    {
        var list = new List<MonitorRect>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMon, _, _, _) =>
            {
                var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(hMon, ref info))
                {
                    var r = info.rcMonitor;
                    list.Add(new MonitorRect(
                        r.Left, r.Top,
                        Math.Max(1, r.Right - r.Left),
                        Math.Max(1, r.Bottom - r.Top),
                        (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
                }

                return true;
            }, IntPtr.Zero);
        return list;
    }

    /// <summary>优先返回非主显示器（面积最大者）；若无则 null。</summary>
    public static MonitorRect? FindBestExtendTarget()
    {
        var all = GetAll();
        var secondary = all.Where(m => !m.IsPrimary).OrderByDescending(m => m.Width * (long)m.Height).ToList();
        if (secondary.Count > 0) return secondary[0];
        return null;
    }

    /// <summary>主显示器；无则取第一块。</summary>
    public static MonitorRect? FindPrimary()
    {
        var all = GetAll();
        if (all.Count == 0) return null;
        foreach (var m in all)
            if (m.IsPrimary) return m;
        return all[0];
    }

    public static int Count => GetAll().Count;
}
