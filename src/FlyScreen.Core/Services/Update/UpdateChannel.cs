using System.Runtime.InteropServices;

namespace FlyScreen.Services.Update;

/// <summary>发版资源通道：与 artifacts/release 的 rid 一一对应。</summary>
public enum UpdateChannel
{
    /// <summary>WPF stable：FlyScreen-*-win-x64.exe</summary>
    WinWpf,

    /// <summary>MAUI Windows：FlyScreen-*-maui-win-x64.exe</summary>
    MauiWin,

    /// <summary>MAUI Android：FlyScreen-*-maui-android.apk</summary>
    MauiAndroid,

    /// <summary>MAUI Mac Catalyst：FlyScreen-*-maui-osx-arm64</summary>
    MauiMac,

    /// <summary>Avalonia Mac：osx-arm64 / osx-x64</summary>
    DesktopMac,

    /// <summary>Avalonia Linux：linux-x64 / linux-arm64</summary>
    DesktopLinux
}

public static class UpdateChannelRules
{
    public static string Describe(UpdateChannel channel) => channel switch
    {
        UpdateChannel.WinWpf => "win-x64",
        UpdateChannel.MauiWin => "maui-win-x64",
        UpdateChannel.MauiAndroid => "maui-android",
        UpdateChannel.MauiMac => "maui-osx",
        UpdateChannel.DesktopMac => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "osx-arm64"
            : "osx-x64",
        UpdateChannel.DesktopLinux => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "linux-arm64"
            : "linux-x64",
        _ => "unknown"
    };

    public static bool IsPreferredAsset(UpdateChannel channel, string fileName)
    {
        var n = fileName.ToLowerInvariant();
        return channel switch
        {
            UpdateChannel.WinWpf =>
                !n.Contains("maui") && !n.Contains("linux") && !n.Contains("osx") &&
                !n.Contains("android") && !n.Contains("harmony") &&
                !n.EndsWith(".apk") && !n.EndsWith(".hap") &&
                ((n.EndsWith(".exe") && n.Contains("win-x64")) || n is "flyscreen.exe" or "xuanniao.exe"),

            UpdateChannel.MauiWin =>
                n.Contains("maui-win") && n.EndsWith(".exe"),

            UpdateChannel.MauiAndroid =>
                n.Contains("maui-android") && n.EndsWith(".apk"),

            UpdateChannel.MauiMac =>
                n.Contains("maui-osx") && !n.EndsWith(".apk") && !n.EndsWith(".exe"),

            UpdateChannel.DesktopMac =>
                !n.Contains("maui") && !n.EndsWith(".apk") && !n.EndsWith(".exe") &&
                n.Contains(Describe(UpdateChannel.DesktopMac)),

            UpdateChannel.DesktopLinux =>
                !n.Contains("maui") && !n.EndsWith(".apk") && !n.EndsWith(".exe") &&
                n.Contains(Describe(UpdateChannel.DesktopLinux)),

            _ => false
        };
    }

    /// <summary>Avalonia Desktop 通道（勿回落到 WinWpf，避免误下 WPF 包）。</summary>
    public static UpdateChannel DetectDesktopChannel()
    {
        if (OperatingSystem.IsMacOS()) return UpdateChannel.DesktopMac;
        if (OperatingSystem.IsLinux()) return UpdateChannel.DesktopLinux;
        // Windows 上跑 Avalonia 多为调试；无对应发版 rid，检查更新时不会匹配 win-x64
        return UpdateChannel.DesktopMac;
    }
}

