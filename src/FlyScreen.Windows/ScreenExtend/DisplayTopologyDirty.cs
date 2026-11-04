using FlyScreen.Services;

namespace FlyScreen.ScreenExtend;

/// <summary>
/// 扩展投影（含 VDD）进行中的持久脏标记。
/// 进程被杀/崩溃时来不及 <c>/internal</c>，下次启动据此收拢「仅电脑屏幕」。
/// </summary>
public static class DisplayTopologyDirty
{
    public static string MarkerPath => Path.Combine(FlyScreenDataPaths.Root, "display-topology-extend.dirty");

    public static bool IsMarked
    {
        get
        {
            try { return File.Exists(MarkerPath); }
            catch { return false; }
        }
    }

    public static void Mark()
    {
        try
        {
            Directory.CreateDirectory(FlyScreenDataPaths.Root);
            File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o") + Environment.NewLine);
        }
        catch
        {
            // ignore
        }

        // 主进程被杀 → TopologyGuard /internal；Guard 被先杀 → 保活定时重拉
        DisplayTopologyWatchdog.EnsureRunning();
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(MarkerPath))
                File.Delete(MarkerPath);
        }
        catch
        {
            // ignore
        }
    }
}
