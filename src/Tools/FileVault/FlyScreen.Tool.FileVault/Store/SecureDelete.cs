namespace FlyScreen.Tool.FileVault.Store;

/// <summary>尽力安全删除。SSD/TRIM 下无法保证物理不可恢复。</summary>
internal static class SecureDelete
{
    public static void File(string path, int passes = 1)
    {
        if (!System.IO.File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 0)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                var buf = new byte[Math.Min(1024 * 1024, (int)Math.Min(info.Length, int.MaxValue))];
                for (var p = 0; p < passes; p++)
                {
                    fs.Position = 0;
                    long remaining = info.Length;
                    while (remaining > 0)
                    {
                        var n = (int)Math.Min(buf.Length, remaining);
                        Random.Shared.NextBytes(buf.AsSpan(0, n));
                        fs.Write(buf, 0, n);
                        remaining -= n;
                    }
                    fs.Flush(true);
                }
            }
        }
        catch
        {
            // 仍尝试删除
        }

        try { System.IO.File.Delete(path); }
        catch { /* ignore */ }
    }

    public static void DirectoryTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File(file);
        try { Directory.Delete(path, recursive: true); }
        catch
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
                }
                Directory.Delete(path, recursive: true);
            }
            catch { /* ignore */ }
        }
    }
}
