#if ANDROID
using FlyScreen.Services;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 数据根：<c>Android/data/com.xuanniao.flyscreen/files</c>（GetExternalFilesDir）。
/// 无需「所有文件访问」；卸载系统会清理。部分机型文件管理可浏览 Android/data。
/// </summary>
public static class AndroidPublicDataRoot
{
    public const string FolderName = "com.xuanniao.flyscreen";

    /// <summary>当前数据根（若已成功初始化）。</summary>
    public static string? CurrentRoot { get; private set; }

    /// <summary>
    /// 切到应用外部私有目录。须在 <see cref="MauiAppHost"/> 加载设置/传输之前调用。
    /// </summary>
    public static bool TryBootstrap()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            if (ctx == null) return false;

            // …/Android/data/com.xuanniao.flyscreen/files —— 无需 MANAGE_EXTERNAL_STORAGE
            var root = ctx.GetExternalFilesDir(null)?.AbsolutePath
                       ?? ctx.FilesDir?.AbsolutePath;
            if (string.IsNullOrWhiteSpace(root))
                return false;

            Directory.CreateDirectory(root);

            var probe = Path.Combine(root, ".flyscreen_write_ok");
            File.WriteAllText(probe, DateTime.UtcNow.ToString("O"));
            try { File.Delete(probe); } catch { /* ignore */ }

            FlyScreenDataPaths.SetRoot(root);
            FlyScreenDataPaths.TryMigrateFromLegacyAppData();
            TryMigrateFromOldStorageRoot(root);

            // 收件箱 → 下载/XuanNiao/Inbox（共享文件可直接在文件管理查看）
            var oldInbox = Path.Combine(root, "Inbox");
            var publicInbox = AndroidXuanniaoDownloads.TryEnsureSubdir("Inbox");
            if (!string.IsNullOrEmpty(publicInbox))
            {
                FlyScreenDataPaths.SetInboxDirectory(publicInbox);
                TryMigrateInbox(oldInbox, publicInbox);
            }

            // 预创建抽样目录
            AndroidXuanniaoDownloads.TryEnsureSubdir("ocr_samples");

            CurrentRoot = root;
            return true;
        }
        catch (Exception ex)
        {
            try { global::Android.Util.Log.Warn("FlyData", "data root: " + ex.Message); } catch { /* ignore */ }
            CurrentRoot = null;
            return false;
        }
    }

    /// <summary>
    /// 若曾用「内部存储根/com.xuanniao.flyscreen」且新根尚无 settings，则尽量拷过来（旧目录可读时）。
    /// </summary>
    private static void TryMigrateFromOldStorageRoot(string newRoot)
    {
        try
        {
            if (File.Exists(Path.Combine(newRoot, "settings.json")))
                return;

            var storage = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath
                          ?? "/storage/emulated/0";
            var oldRoot = Path.Combine(storage, FolderName);
            if (!Directory.Exists(oldRoot))
                return;
            if (string.Equals(
                    Path.GetFullPath(oldRoot),
                    Path.GetFullPath(newRoot),
                    StringComparison.OrdinalIgnoreCase))
                return;

            CopyDirectoryRecursive(oldRoot, newRoot);
            try
            {
                global::Android.Util.Log.Info("FlyData", "migrated from old storage root " + oldRoot);
            }
            catch { /* ignore */ }
        }
        catch
        {
            /* 无所有文件权限时旧根可能读不到，忽略 */
        }
    }

    private static void TryMigrateInbox(string oldInbox, string newInbox)
    {
        try
        {
            if (!Directory.Exists(oldInbox)) return;
            if (string.Equals(
                    Path.GetFullPath(oldInbox),
                    Path.GetFullPath(newInbox),
                    StringComparison.OrdinalIgnoreCase))
                return;
            CopyDirectoryRecursive(oldInbox, newInbox);
        }
        catch
        {
            /* ignore */
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(file));
            if (!File.Exists(dest))
                File.Copy(file, dest, overwrite: false);
        }

        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(sub);
            if (string.IsNullOrEmpty(name)) continue;
            CopyDirectoryRecursive(sub, Path.Combine(destDir, name));
        }
    }
}
#endif
