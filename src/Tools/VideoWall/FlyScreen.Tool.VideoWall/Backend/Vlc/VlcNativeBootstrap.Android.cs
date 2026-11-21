using System.Reflection;
using System.Runtime.InteropServices;
using FlyScreen.Services;
using LibVLCSharp.Shared;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

/// <summary>
/// Android：优先信任宿主已完成的 Java Load + JNI_OnLoad。
/// 工具 ALC 内反射 <c>JavaSystem.Load</c> 会 ClassLoader=null，故此处不再强行 Initialize / 假标成功。
/// </summary>
internal static partial class VlcNativeBootstrap
{
    private const string HostReadyDataKey = "FlyScreen.LibVLC.AndroidReady";
    private static bool _resolverInstalled;

    private static bool TryInitializeAndroid()
    {
        // 部署探针：若仍看到「强制标记已加载」说明设备在跑旧 .flytool
        AppLog.Info("视频墙：Android 引导 v0.1.11（需宿主 Java Load + JNI_OnLoad）");

        if (AppContext.GetData(HostReadyDataKey) is true
            || string.Equals(AppContext.GetData(HostReadyDataKey)?.ToString(), "True", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info("视频墙：宿主已声明 LibVLC Android 就绪，跳过工具侧装库");
            LastResolvedDirectory = TryGetAndroidNativeLibraryDir();
            return ProbeLibVlc();
        }

        var dir = TryGetAndroidNativeLibraryDir();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            AppLog.Warn($"视频墙：NativeLibraryDir 无效：{dir ?? "(null)"}；请确认宿主 LibVlcAndroidBootstrap.Ensure 已跑通");
            return false;
        }

        LastResolvedDirectory = dir;
        var cpp = Path.Combine(dir, "libc++_shared.so");
        var vlc = Path.Combine(dir, "libvlc.so");
        AppLog.Info($"视频墙：Android 原生目录 {dir} cpp={File.Exists(cpp)} vlc={File.Exists(vlc)}");

        // 仅作 P/Invoke 后备；真正 JNI 装库必须在宿主完成
        TryNativeLoadPath(cpp);
        TryNativeLoadPath(vlc);
        TryInstallDllImportResolver(dir);

        if (IsCoreMarkedLoaded())
        {
            AppLog.Info("视频墙：Core 已标记加载（宿主路径）");
            return ProbeLibVlc();
        }

        // 不再调用 Core.Initialize()：短名 LoadLibrary("c++_shared") 会失败并误导后续强标
        // 不再 TryForceMarkCoreLoaded：无 JNI_OnLoad 时探测能过但 Play 卡在「连接中」
        AppLog.Warn(
            "视频墙：宿主未完成 LibVLC Android 初始化（缺 JavaSystem.Load / JNI_OnLoad）。" +
            "请确认 MainActivity/MauiProgram 已调用 LibVlcAndroidBootstrap.Ensure，且日志含「JNI_OnLoad OK」。");
        return false;
    }

    private static bool ProbeLibVlc()
    {
        try
        {
            using var probe = new LibVLC("--quiet");
            AppLog.Info("视频墙：LibVLC 探测实例 OK");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"视频墙：LibVLC 探测失败：{ex.Message}");
            return false;
        }
    }

    private static bool IsCoreMarkedLoaded()
    {
        try
        {
            var t = typeof(Core);
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (f.FieldType != typeof(bool)) continue;
                var n = f.Name;
                if (!n.Contains("load", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("init", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (f.GetValue(null) is true)
                    return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    private static void TryInstallDllImportResolver(string nativeDir)
    {
        if (_resolverInstalled) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Core).Assembly, (libraryName, _, _) =>
            {
                var file = ResolveAndroidSoFileName(libraryName);
                if (file is null) return IntPtr.Zero;
                var path = Path.Combine(nativeDir, file);
                if (!File.Exists(path)) return IntPtr.Zero;
                try { return NativeLibrary.Load(path); }
                catch { return IntPtr.Zero; }
            });
            _resolverInstalled = true;
            AppLog.Info("视频墙：已安装 LibVLCSharp DllImportResolver → NativeLibraryDir");
        }
        catch (Exception ex)
        {
            AppLog.Info($"视频墙：DllImportResolver 沿用已有（{ex.Message}）");
            _resolverInstalled = true;
        }
    }

    private static string? ResolveAndroidSoFileName(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName)) return null;
        var n = Path.GetFileName(libraryName.Trim());
        if (n.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            n = n[..^3];

        if (n.Equals("c++_shared", StringComparison.OrdinalIgnoreCase)
            || n.Equals("libc++_shared", StringComparison.OrdinalIgnoreCase))
            return "libc++_shared.so";
        if (n.Equals("vlc", StringComparison.OrdinalIgnoreCase)
            || n.Equals("libvlc", StringComparison.OrdinalIgnoreCase))
            return "libvlc.so";
        if (n.Equals("vlccore", StringComparison.OrdinalIgnoreCase)
            || n.Equals("libvlccore", StringComparison.OrdinalIgnoreCase))
            return "libvlccore.so";

        if (!n.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            return "lib" + n + ".so";
        return n + ".so";
    }

    private static bool TryNativeLoadPath(string absoluteSoPath)
    {
        if (!File.Exists(absoluteSoPath)) return false;
        try
        {
            NativeLibrary.Load(absoluteSoPath);
            AppLog.Info($"视频墙：NativeLibrary.Load OK ← {absoluteSoPath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info($"视频墙：NativeLibrary.Load（可能已加载）：{ex.Message} ← {absoluteSoPath}");
            return true;
        }
    }

    private static string? TryGetAndroidNativeLibraryDir()
    {
        try
        {
            var appType = Type.GetType("Android.App.Application, Mono.Android")
                ?? Type.GetType("Android.App.Application, Mono.Android.dll");
            var ctx = appType?.GetProperty("Context", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (ctx is null) return null;
            var appInfo = ctx.GetType().GetProperty("ApplicationInfo")?.GetValue(ctx);
            return appInfo?.GetType().GetProperty("NativeLibraryDir")?.GetValue(appInfo) as string;
        }
        catch
        {
            return null;
        }
    }
}
