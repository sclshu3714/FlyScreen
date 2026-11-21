using System.Runtime.InteropServices;
using FlyScreen.Services;
using LibVLCSharp.Shared;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

/// <summary>
/// 解析 libvlc 原生库路径并执行 <see cref="Core.Initialize"/>（单进程只初始化一次）。
/// 工具经 ALC <c>LoadFromStream</c> 加载时 <see cref="Assembly.Location"/> 为空，
/// MUST 依赖宿主写入的安装目录或 <see cref="FlyScreenDataPaths.ToolsInstalledDirectory"/>。
/// <para>
/// Windows：原生库优先落在 <c>Tools/_native/libvlc/win-x64</c> 再 Initialize。
/// Android：见 <c>VlcNativeBootstrap.Android.cs</c> —— 绝对路径 System.load，短名 LoadLibrary 不可靠。
/// </para>
/// </summary>
internal static partial class VlcNativeBootstrap
{
    private const string InstallDirDataKey = "FlyScreen.Tool.InstallDir.video-wall";

    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _available;
    private static string? _nativeRootOverride;

    /// <summary>显式指定含原生库子目录的工具安装根目录（Windows 为 <c>libvlc/win-x64</c>）。</summary>
    public static void SetNativeRoot(string? installDir)
    {
        lock (Gate)
        {
            _nativeRootOverride = string.IsNullOrWhiteSpace(installDir) ? null : installDir.Trim();
            if (!_available)
                _initialized = false;
        }
    }

    public static bool TryEnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
                return _available;

            _initialized = true;
            try
            {
                if (OperatingSystem.IsAndroid())
                {
                    _available = TryInitializeAndroid();
                    return _available;
                }

                var dir = ResolveLibVlcDirectory();
                if (!string.IsNullOrEmpty(dir))
                    Core.Initialize(dir);
                else
                    Core.Initialize();

                _available = true;
            }
            catch (Exception ex)
            {
                _available = false;
                AppLog.Warn($"视频墙：LibVLC Initialize 失败：{ex.Message}");
            }

            return _available;
        }
    }

    public static string? LastResolvedDirectory { get; private set; }

    private static string? ResolveLibVlcDirectory()
    {
        var rid = NativeRidFolder;
        var nativeFile = NativeLibFileName;

        // 1) 已有稳定缓存 → 直接用（不锁 Installed）
        var cached = NativeCacheLibVlcDir;
        if (File.Exists(Path.Combine(cached, nativeFile)))
        {
            LastResolvedDirectory = cached;
            return cached;
        }

        // 2) 从安装目录候选找到源，拷入缓存后再 Initialize
        foreach (var root in EnumerateCandidateRoots())
        {
            var hit = FindLibVlcUnder(root);
            if (hit == null) continue;

            var seeded = TrySeedNativeCache(hit);
            if (!string.IsNullOrEmpty(seeded))
            {
                LastResolvedDirectory = seeded;
                return seeded;
            }

            // 拷贝失败则退回源路径（本进程仍可能锁 Installed，但至少能播）
            LastResolvedDirectory = hit;
            return hit;
        }

        LastResolvedDirectory = null;
        return null;
    }

    private static string NativeRidFolder
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return "win-x64";
            if (OperatingSystem.IsAndroid())
            {
                return RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.Arm64 => "android-arm64",
                    Architecture.Arm => "android-arm",
                    Architecture.X64 => "android-x64",
                    _ => "android-arm64"
                };
            }

            return "win-x64";
        }
    }

    private static string NativeLibFileName
        => OperatingSystem.IsWindows() ? "libvlc.dll" : "libvlc.so";

    private static string NativeCacheLibVlcDir
        => Path.Combine(FlyScreenDataPaths.ToolsNativeDirectory, "libvlc", NativeRidFolder);

    private static string? TrySeedNativeCache(string sourceLibVlcDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceLibVlcDir) || !Directory.Exists(sourceLibVlcDir))
                return null;

            var nativeFile = NativeLibFileName;
            var srcDll = Path.Combine(sourceLibVlcDir, nativeFile);
            if (!File.Exists(srcDll))
                return null;

            var dest = NativeCacheLibVlcDir;
            var stampPath = Path.Combine(FlyScreenDataPaths.ToolsNativeDirectory, "libvlc", "source.stamp");
            var stamp = $"{Path.GetFullPath(sourceLibVlcDir)}|{File.GetLastWriteTimeUtc(srcDll).Ticks}|{new FileInfo(srcDll).Length}";

            if (File.Exists(Path.Combine(dest, nativeFile))
                && File.Exists(stampPath)
                && string.Equals(File.ReadAllText(stampPath).Trim(), stamp, StringComparison.Ordinal))
                return dest;

            Directory.CreateDirectory(FlyScreenDataPaths.ToolsNativeDirectory);
            var staging = dest + ".staging-" + DateTime.UtcNow.Ticks;
            try
            {
                CopyDirectory(sourceLibVlcDir, staging);
                if (Directory.Exists(dest))
                {
                    try { Directory.Delete(dest, recursive: true); }
                    catch
                    {
                        // 旧缓存若正被本进程占用，直接沿用
                        if (File.Exists(Path.Combine(dest, nativeFile)))
                        {
                            try { Directory.Delete(staging, recursive: true); } catch { /* ignore */ }
                            return dest;
                        }
                    }
                }

                Directory.Move(staging, dest);
                Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
                File.WriteAllText(stampPath, stamp);
                AppLog.Info($"视频墙：LibVLC 已缓存 → {dest}");
                return dest;
            }
            catch
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch { /* ignore */ }
                throw;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"视频墙：缓存 LibVLC 失败：{ex.Message}");
            return null;
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destDir, name), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(destDir, name));
        }
    }

    private static IEnumerable<string> EnumerateCandidateRoots()
    {
        var list = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!list.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                list.Add(path);
        }

        Add(_nativeRootOverride);

        if (AppContext.GetData(InstallDirDataKey) is string fromHost)
            Add(fromHost);

        try
        {
            var installedRoot = Path.Combine(FlyScreenDataPaths.ToolsInstalledDirectory, "video-wall");
            if (Directory.Exists(installedRoot))
            {
                foreach (var verDir in Directory.EnumerateDirectories(installedRoot)
                             .Where(d => !Path.GetFileName(d).StartsWith(".orphaned-", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                    Add(verDir);
            }
        }
        catch { /* ignore */ }

        try
        {
            var asm = typeof(VlcNativeBootstrap).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(asm))
                Add(Path.GetDirectoryName(asm));
        }
        catch { /* ignore */ }

        Add(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return list;
    }

    private static string? FindLibVlcUnder(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        var rid = NativeRidFolder;
        var nativeFile = NativeLibFileName;
        var candidates = new[]
        {
            Path.Combine(root, "libvlc", rid),
            Path.Combine(root, "runtimes", rid, "native"),
            root
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, nativeFile)))
                return dir;
        }

        return null;
    }
}
