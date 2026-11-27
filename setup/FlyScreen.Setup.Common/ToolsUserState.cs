using System.IO;

namespace FlyScreen.Setup.Common;

/// <summary>
/// 升级时保留安装目录旁 Tools 用户态（已装插件、ToolData、快捷栏等）。
/// 新包只带 Bundled；Installed/ToolData 必须从旧版合并回来。
/// </summary>
public static class ToolsUserState
{
    public static readonly string[] PreserveFileNames =
    [
        "tools-install.json",
        "tools-shortcuts.json",
        "tools-local-programs.json",
        "tools-index.cache.json"
    ];

    public static readonly string[] PreserveDirectoryNames =
    [
        "Installed",
        "ToolData",
        "_native"
    ];

    /// <summary>
    /// 从旧安装根（或 backup 根）捕获 Tools 用户态到 <paramref name="captureRoot"/>/ToolsUserState。
    /// 若无可保留内容返回 null。
    /// </summary>
    public static string? Capture(string oldInstallRoot, string captureRoot)
    {
        var tools = FindToolsRoot(oldInstallRoot);
        if (tools == null) return null;

        var dest = System.IO.Path.Combine(captureRoot, "ToolsUserState");
        if (Directory.Exists(dest))
            TryDeleteDirectory(dest);
        Directory.CreateDirectory(dest);

        var any = false;
        foreach (var name in PreserveFileNames)
        {
            var src = System.IO.Path.Combine(tools, name);
            if (!File.Exists(src)) continue;
            File.Copy(src, System.IO.Path.Combine(dest, name), overwrite: true);
            any = true;
        }

        foreach (var name in PreserveDirectoryNames)
        {
            var src = System.IO.Path.Combine(tools, name);
            if (!Directory.Exists(src)) continue;
            CopyDirectory(src, System.IO.Path.Combine(dest, name));
            any = true;
        }

        if (!any)
        {
            TryDeleteDirectory(dest);
            return null;
        }

        return dest;
    }

    /// <summary>将捕获的用户态合并进新安装的 Tools（不覆盖新包 Bundled）。</summary>
    public static void Restore(string? captureDir, string newInstallRoot)
    {
        if (string.IsNullOrWhiteSpace(captureDir) || !Directory.Exists(captureDir))
            return;

        var tools = System.IO.Path.Combine(newInstallRoot, "Tools");
        Directory.CreateDirectory(tools);

        foreach (var name in PreserveFileNames)
        {
            var src = System.IO.Path.Combine(captureDir, name);
            if (!File.Exists(src)) continue;
            File.Copy(src, System.IO.Path.Combine(tools, name), overwrite: true);
        }

        foreach (var name in PreserveDirectoryNames)
        {
            var src = System.IO.Path.Combine(captureDir, name);
            if (!Directory.Exists(src)) continue;
            var dest = System.IO.Path.Combine(tools, name);
            if (Directory.Exists(dest))
                TryDeleteDirectory(dest);
            CopyDirectory(src, dest);
        }
    }

    public static string? FindToolsRoot(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return null;

        var direct = System.IO.Path.Combine(installRoot, "Tools");
        if (Directory.Exists(direct))
            return direct;

        // 少数布局：exe 在子目录
        foreach (var dir in Directory.EnumerateDirectories(installRoot, "Tools", SearchOption.AllDirectories))
            return dir;

        return null;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(sourceDir, file);
            var dest = System.IO.Path.Combine(destDir, rel);
            var parent = System.IO.Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            }
            Directory.Delete(path, recursive: true);
        }
        catch { /* ignore */ }
    }
}
