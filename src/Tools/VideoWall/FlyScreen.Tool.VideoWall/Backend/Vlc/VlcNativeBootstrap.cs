using System.Reflection;
using LibVLCSharp.Shared;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

/// <summary>解析 libvlc 原生库路径并执行 <see cref="Core.Initialize"/>（单进程只初始化一次）。</summary>
internal static class VlcNativeBootstrap
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _available;

    public static bool TryEnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
                return _available;

            _initialized = true;
            try
            {
                var dir = ResolveLibVlcDirectory();
                if (!string.IsNullOrEmpty(dir))
                    Core.Initialize(dir);
                else
                    Core.Initialize();

                _available = true;
            }
            catch
            {
                _available = false;
            }

            return _available;
        }
    }

    private static string? ResolveLibVlcDirectory()
    {
        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(asmDir))
            return null;

        var candidates = new[]
        {
            Path.Combine(asmDir, "libvlc", "win-x64"),
            Path.Combine(asmDir, "runtimes", "win-x64", "native"),
            asmDir
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "libvlc.dll")))
                return dir;
        }

        return null;
    }
}