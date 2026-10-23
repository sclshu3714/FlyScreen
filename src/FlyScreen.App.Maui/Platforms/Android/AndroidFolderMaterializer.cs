using Android.Content;
using Android.Provider;
using AndroidX.DocumentFile.Provider;
using FlyScreen.AppMaui.Helpers;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// SAF 选文件夹并拷到 Cache，供直接传输使用可读本地路径。
/// </summary>
internal static class AndroidFolderMaterializer
{
    public static async Task<string?> PickAndMaterializeAsync(CancellationToken ct = default)
    {
        var treeUri = await MainActivity.PickDocumentTreeAsync().ConfigureAwait(true);
        if (treeUri is null) return null;

        var ctx = global::Android.App.Application.Context
                  ?? throw new InvalidOperationException("Android Context unavailable.");

        try
        {
            ctx.ContentResolver?.TakePersistableUriPermission(
                treeUri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch
        {
            // 部分提供方不支持持久授权
        }

        var tree = DocumentFile.FromTreeUri(ctx, treeUri);
        if (tree is null || !tree.Exists())
            return null;

        var folderName = MauiPickedFileStore.SanitizeFileName(tree.Name ?? "folder");
        var destRoot = Path.Combine(
            FileSystem.CacheDirectory,
            "FlyScreenDt",
            Guid.NewGuid().ToString("N"),
            folderName);
        Directory.CreateDirectory(destRoot);

        await Task.Run(() => CopyDocumentRecursive(ctx, tree, destRoot, ct), ct).ConfigureAwait(false);
        return Directory.Exists(destRoot) ? destRoot : null;
    }

    private static void CopyDocumentRecursive(
        Context ctx,
        DocumentFile dir,
        string destDir,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var children = dir.ListFiles() ?? [];
        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            if (child is null) continue;

            var name = MauiPickedFileStore.SanitizeFileName(child.Name ?? Guid.NewGuid().ToString("N"));
            if (child.IsDirectory)
            {
                CopyDocumentRecursive(ctx, child, Path.Combine(destDir, name), ct);
                continue;
            }

            if (!child.IsFile) continue;
            var uri = child.Uri;
            if (uri is null) continue;

            var dest = Path.Combine(destDir, name);
            using var src = ctx.ContentResolver?.OpenInputStream(uri);
            if (src is null) continue;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }
}
