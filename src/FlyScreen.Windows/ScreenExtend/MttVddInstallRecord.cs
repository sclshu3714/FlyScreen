using FlyScreen.Services;

namespace FlyScreen.ScreenExtend;

/// <summary>
/// 记录本机 MTT/VDD 是否由玄鸟「环境修复」安装。
/// 用户本机预装的驱动不写此标记，投影弹窗不提供卸载。
/// </summary>
public static class MttVddInstallRecord
{
    public static string MarkerPath => Path.Combine(FlyScreenDataPaths.Root, "mtt-vdd-installed-by-app.flag");

    public static bool IsInstalledByApp
    {
        get
        {
            try { return File.Exists(MarkerPath); }
            catch { return false; }
        }
    }

    public static void MarkInstalledByApp()
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
