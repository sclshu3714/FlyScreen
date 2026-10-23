namespace FlyScreen.AppMaui.Helpers;

/// <summary>
/// 将 FilePicker 结果落到可 File.Exists / 传输的本地路径。
/// Android/iOS 常为 content:// 或安全作用域，不能直接用 FullPath。
/// </summary>
internal static class MauiPickedFileStore
{
    public static async Task<string?> MaterializeAsync(FileResult file, CancellationToken ct = default)
    {
        if (file is null) return null;

        try
        {
            var full = file.FullPath;
            if (!string.IsNullOrWhiteSpace(full)
                && !full.StartsWith("content:", StringComparison.OrdinalIgnoreCase)
                && File.Exists(full))
                return full;
        }
        catch
        {
            // ignore
        }

        var name = string.IsNullOrWhiteSpace(file.FileName)
            ? Guid.NewGuid().ToString("N")
            : Path.GetFileName(file.FileName);
        name = SanitizeFileName(name);

        var dir = Path.Combine(FileSystem.CacheDirectory, "FlyScreenPick", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, name);

        await using (var src = await file.OpenReadAsync().ConfigureAwait(false))
        await using (var dst = File.Create(dest))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);

        return File.Exists(dest) ? dest : null;
    }

    public static async Task<IReadOnlyList<string>> MaterializeManyAsync(
        IEnumerable<FileResult> files,
        CancellationToken ct = default)
    {
        var list = new List<string>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var path = await MaterializeAsync(f, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
                list.Add(path);
        }

        return list;
    }

    internal static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.bin" : name;
    }
}
