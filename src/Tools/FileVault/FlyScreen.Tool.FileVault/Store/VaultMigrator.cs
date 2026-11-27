namespace FlyScreen.Tool.FileVault.Store;

internal static class VaultMigrator
{
    public static async Task MigrateAsync(
        string sourceRoot,
        string destRoot,
        IProgress<VaultProgress>? progress,
        CancellationToken ct)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        destRoot = Path.GetFullPath(destRoot);

        if (string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase))
            return;
        if (!VaultRepository.LooksLikeVault(sourceRoot))
            throw new InvalidOperationException("源不是保险箱");
        if (Directory.Exists(destRoot) && Directory.EnumerateFileSystemEntries(destRoot).Any())
            throw new InvalidOperationException("目标目录非空");

        Directory.CreateDirectory(destRoot);

        var sameVolume = string.Equals(
            Path.GetPathRoot(sourceRoot),
            Path.GetPathRoot(destRoot),
            StringComparison.OrdinalIgnoreCase);

        if (sameVolume)
        {
            progress?.Report(new VaultProgress { Phase = "migrate", CurrentName = "move" });
            Directory.Move(sourceRoot, destRoot);
            return;
        }

        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToList();
        long total = files.Sum(f => new FileInfo(f).Length);
        long done = 0;

        try
        {
            foreach (var src in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(sourceRoot, src);
                var dst = Path.Combine(destRoot, rel);
                var dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                await using (var input = File.OpenRead(src))
                await using (var output = File.Create(dst))
                {
                    var buf = new byte[1024 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        progress?.Report(new VaultProgress
                        {
                            Phase = "migrate",
                            CurrentName = rel,
                            Done = done,
                            Total = total
                        });
                    }
                }

                if (new FileInfo(src).Length != new FileInfo(dst).Length)
                    throw new IOException($"校验失败：{rel}");
            }

            // 复制空目录
            foreach (var dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceRoot, dir);
                Directory.CreateDirectory(Path.Combine(destRoot, rel));
            }

            if (!VaultRepository.LooksLikeVault(destRoot))
                throw new InvalidDataException("迁移后目标不是有效保险箱");

            SecureDelete.DirectoryTree(sourceRoot);
        }
        catch
        {
            try { SecureDelete.DirectoryTree(destRoot); } catch { /* ignore */ }
            throw;
        }
    }

    public static long EstimateSize(string root)
    {
        if (!Directory.Exists(root)) return 0;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }
}
