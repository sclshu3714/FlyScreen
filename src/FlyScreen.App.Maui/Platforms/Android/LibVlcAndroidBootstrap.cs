using System.Reflection;
using System.Runtime.InteropServices;
using FlyScreen.Services;
using Java.Interop;
using LibVLCSharp.Shared;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 宿主进程尽早装入 LibVLC 原生库。
/// <para>
/// LibVLCSharp 的 <c>Core.Initialize()</c> 在 Android 上会走 <c>LoadLibrary("c++_shared")</c>（短名），
/// 在 .NET MAUI / 模拟器上经常 dlopen 失败；即便 <c>NativeLibrary.Load(绝对路径)</c> 已成功也无济于事。
/// 正确路径：<c>JavaSystem.Load(绝对路径)</c> → <c>JNI_OnLoad</c> → 标记 Core 已加载。
/// 工具 ALC 内反射 Java Load 会因 ClassLoader 为空失败，故必须在宿主完成。
/// </para>
/// </summary>
internal static class LibVlcAndroidBootstrap
{
    internal const string ReadyDataKey = "FlyScreen.LibVLC.AndroidReady";

    private static readonly object Gate = new();
    private static bool _succeeded;
    private static bool _resolverInstalled;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JniOnLoadFn(IntPtr javaVm, IntPtr reserved);

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_succeeded) return;

            var dir = global::Android.App.Application.Context?.ApplicationInfo?.NativeLibraryDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                // 不置成功：MauiProgram 过早调用时 Context 可能未就绪，留给 MainActivity 再试
                HostLog("warn", $"NativeLibraryDir 尚未就绪（{dir ?? "null"}），稍后重试");
                return;
            }

            var cpp = Path.Combine(dir, "libc++_shared.so");
            var vlc = Path.Combine(dir, "libvlc.so");
            if (!File.Exists(cpp) || !File.Exists(vlc))
            {
                HostLog("warn", $"缺少原生库 cpp={File.Exists(cpp)} vlc={File.Exists(vlc)} dir={dir}");
                return;
            }

            if (!TryJavaLoad(cpp) || !TryJavaLoad(vlc))
            {
                HostLog("warn", "JavaSystem.Load 失败，LibVLC 不可用");
                return;
            }

            // .NET 侧握句柄：DllImportResolver + 导出 JNI_OnLoad
            TryNativeLoad(cpp);
            if (!NativeLibrary.TryLoad(vlc, out var vlcHandle))
            {
                try { vlcHandle = NativeLibrary.Load(vlc); }
                catch (Exception ex)
                {
                    HostLog("warn", $"无法 Load libvlc.so：{ex.Message}");
                    return;
                }
            }

            TryInstallResolver(dir);

            if (!TryJniOnLoad(vlcHandle))
            {
                HostLog("warn", "JNI_OnLoad 失败，跳过强标（避免无 JNI 假成功）");
                return;
            }

            if (!TryMarkCoreLoaded())
            {
                HostLog("warn", "未能标记 Core 已加载");
                return;
            }

            try
            {
                using var probe = new LibVLC("--quiet");
                HostLog("info", $"LibVLC 就绪 ← {dir}");
            }
            catch (Exception ex)
            {
                HostLog("warn", $"LibVLC 探测失败：{ex.Message}");
                return;
            }

            AppContext.SetData(ReadyDataKey, true);
            _succeeded = true;
        }
    }

    private static void HostLog(string level, string message)
    {
        var line = "视频墙宿主：" + message;
        try
        {
            if (level == "warn") AppLog.Warn(line);
            else AppLog.Info(line);
        }
        catch { /* AppLog 未就绪 */ }

        try
        {
            if (level == "warn")
                global::Android.Util.Log.Warn("FlyScreen.LibVLC", line);
            else
                global::Android.Util.Log.Info("FlyScreen.LibVLC", line);
        }
        catch { /* ignore */ }
    }

    private static bool TryJavaLoad(string absoluteSoPath)
    {
        try
        {
            Java.Lang.JavaSystem.Load(absoluteSoPath);
            HostLog("info", $"JavaSystem.Load OK ← {Path.GetFileName(absoluteSoPath)}");
            return true;
        }
        catch (Java.Lang.Throwable ex)
        {
            // 已加载时部分 ROM 会抛；若文件在且曾成功过，继续
            var msg = ex.Message ?? ex.ToString();
            if (msg.Contains("already loaded", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("already been loaded", StringComparison.OrdinalIgnoreCase))
            {
                HostLog("info", $"JavaSystem.Load 已装入 ← {Path.GetFileName(absoluteSoPath)}");
                return true;
            }

            HostLog("warn", $"JavaSystem.Load 失败：{msg} ← {Path.GetFileName(absoluteSoPath)}");
            return false;
        }
        catch (Exception ex)
        {
            HostLog("warn", $"JavaSystem.Load 异常：{ex.Message} ← {Path.GetFileName(absoluteSoPath)}");
            return false;
        }
    }

    private static void TryNativeLoad(string absoluteSoPath)
    {
        try { NativeLibrary.Load(absoluteSoPath); }
        catch { /* 可能已加载 */ }
    }

    private static bool TryJniOnLoad(IntPtr vlcHandle)
    {
        try
        {
            var vm = JniRuntime.CurrentRuntime?.InvocationPointer ?? IntPtr.Zero;
            if (vm == IntPtr.Zero)
            {
                HostLog("warn", "JniRuntime.InvocationPointer 为空");
                return false;
            }

            // 不用 [DllImport("vlc")]：resolver 只挂在 LibVLCSharp 程序集上，宿主 P/Invoke 解析不到
            if (!NativeLibrary.TryGetExport(vlcHandle, "JNI_OnLoad", out var export) || export == IntPtr.Zero)
            {
                HostLog("warn", "libvlc.so 无 JNI_OnLoad 导出");
                return false;
            }

            var fn = Marshal.GetDelegateForFunctionPointer<JniOnLoadFn>(export);
            var rc = fn(vm, IntPtr.Zero);
            // JNI 规范：负数失败；LibVLC 通常返回 JNI_VERSION
            if (rc < 0)
            {
                HostLog("warn", $"JNI_OnLoad 返回 {rc}");
                return false;
            }

            HostLog("info", $"JNI_OnLoad OK (rc=0x{rc:X})");
            return true;
        }
        catch (Exception ex)
        {
            HostLog("warn", $"JNI_OnLoad 异常：{ex.Message}");
            return false;
        }
    }

    private static void TryInstallResolver(string nativeDir)
    {
        if (_resolverInstalled) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Core).Assembly, (libraryName, _, _) =>
            {
                var file = ResolveSoFileName(libraryName);
                if (file is null) return IntPtr.Zero;
                var path = Path.Combine(nativeDir, file);
                if (!File.Exists(path)) return IntPtr.Zero;
                try { return NativeLibrary.Load(path); }
                catch { return IntPtr.Zero; }
            });
            _resolverInstalled = true;
            HostLog("info", "已安装 LibVLCSharp DllImportResolver");
        }
        catch (InvalidOperationException)
        {
            _resolverInstalled = true;
        }
        catch (Exception ex)
        {
            HostLog("warn", $"DllImportResolver 失败：{ex.Message}");
        }
    }

    private static bool TryMarkCoreLoaded()
    {
        try
        {
            // 跳过 Core.Initialize()：其内部 LoadLibrary("c++_shared") 在 MAUI 上仍会短名失败
            var t = typeof(Core);
            var marked = false;
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (f.FieldType != typeof(bool)) continue;
                var n = f.Name;
                if (!n.Contains("load", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("init", StringComparison.OrdinalIgnoreCase))
                    continue;
                f.SetValue(null, true);
                HostLog("info", $"已标记 Core.{n}=true（经 Java Load + JNI_OnLoad）");
                marked = true;
            }

            return marked;
        }
        catch (Exception ex)
        {
            HostLog("warn", $"标记 Core 失败：{ex.Message}");
            return false;
        }
    }

    private static string? ResolveSoFileName(string libraryName)
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
}
