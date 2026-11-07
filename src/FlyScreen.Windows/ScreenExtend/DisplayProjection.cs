using System.Diagnostics;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.ScreenExtend;

/// <summary>Windows：调用 DisplaySwitch.exe 切换投影拓扑（对齐 Win+P）。</summary>
public static class DisplayProjection
{
    public static string ModeLabel(DisplayProjectionMode mode)
        => DisplayProjectionModeInfo.Label(mode);

    /// <summary>调用系统 DisplaySwitch.exe；失败时返回 false（不抛）。</summary>
    public static bool TryApply(DisplayProjectionMode mode)
    {
        var arg = mode switch
        {
            DisplayProjectionMode.Internal => "/internal",
            DisplayProjectionMode.Clone => "/clone",
            DisplayProjectionMode.Extend => "/extend",
            DisplayProjectionMode.External => "/external",
            _ => "/extend"
        };

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "DisplaySwitch.exe",
                Arguments = arg,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryApply(int mode) => TryApply(DisplayProjectionModeInfo.Clamp(mode));
}
