#if ANDROID
using System.Text;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Util;
using FlyScreen.Services;
using AppEnv = Android.OS.Environment;
using MediaUri = Android.Net.Uri;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 公共「下载/XuanNiao」：抽样图、收件箱。用 MediaStore 写入（带 IS_PENDING），不造占位 keep 文件。
/// </summary>
public static class AndroidXuanniaoDownloads
{
    public const string FolderName = "XuanNiao";
    public const string LegacyFolderName = "xuanniao";
    public const string MediaRelativeRoot = "Download/XuanNiao";

    public static string? PhysicalRoot { get; private set; }

    public static string MediaRelative(string subDir)
    {
        subDir = (subDir ?? "").Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(subDir)
            ? MediaRelativeRoot
            : $"{MediaRelativeRoot}/{subDir}";
    }

    /// <summary>尽量创建并可写探测 <c>Download/XuanNiao/{subDir}</c> 物理路径。</summary>
    public static string? TryEnsureSubdir(string subDir)
    {
        subDir = (subDir ?? "").Replace('\\', '/').Trim('/');
        try
        {
            var downloads = AppEnv.GetExternalStoragePublicDirectory(AppEnv.DirectoryDownloads);
            var basePath = downloads?.AbsolutePath;
            if (string.IsNullOrWhiteSpace(basePath))
                return null;

            TryMigrateLegacyFolder(basePath);

            var root = Path.Combine(basePath, FolderName);
            var dir = string.IsNullOrEmpty(subDir) ? root : Path.Combine(root, subDir);
            Directory.CreateDirectory(dir);

            var probe = Path.Combine(dir, ".flyscreen_write_ok");
            File.WriteAllText(probe, DateTime.UtcNow.ToString("O"));
            try { File.Delete(probe); } catch { /* ignore */ }

            PhysicalRoot = root;
            return dir;
        }
        catch (Exception ex)
        {
            Log.Warn("FlyDl", $"ensure {subDir}: {ex.Message}");
            return null;
        }
    }

    /// <summary>旧版 <c>Download/xuanniao</c> → <c>Download/XuanNiao</c>（能搬则搬）。</summary>
    private static void TryMigrateLegacyFolder(string downloadsBase)
    {
        try
        {
            var legacy = Path.Combine(downloadsBase, LegacyFolderName);
            var current = Path.Combine(downloadsBase, FolderName);
            if (!Directory.Exists(legacy))
                return;
            if (string.Equals(
                    Path.GetFullPath(legacy),
                    Path.GetFullPath(current),
                    StringComparison.OrdinalIgnoreCase))
                return;

            if (!Directory.Exists(current))
            {
                Directory.Move(legacy, current);
                return;
            }

            // 两边都在：尽量把旧目录里缺的文件拷过来
            foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(legacy, file);
                var dest = Path.Combine(current, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest))
                    File.Copy(file, dest, overwrite: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("FlyDl", "migrate legacy folder: " + ex.Message);
        }
    }

    /// <summary>发布文件到下载/XuanNiao/{subDir}/。图片优先 MediaStore（OEM 更稳），再试物理拷贝。</summary>
    public static string? PublishFile(string localPath, string subDir, string displayName, string mimeType)
    {
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
        {
            Log.Warn("FlyDl", "publish: local missing " + localPath);
            return null;
        }

        var len = new FileInfo(localPath).Length;
        if (len <= 0)
        {
            Log.Warn("FlyDl", "publish: empty file " + localPath);
            return null;
        }

        // 1) MediaStore（不依赖所有文件访问；IS_PENDING 避免半成品）
        var viaStore = PublishViaMediaStore(localPath, subDir, displayName, mimeType);
        if (!string.IsNullOrEmpty(viaStore))
            return viaStore;

        // 2) 物理路径兜底
        var physicalDir = TryEnsureSubdir(subDir);
        if (!string.IsNullOrEmpty(physicalDir))
        {
            try
            {
                var dest = Path.Combine(physicalDir, displayName);
                File.Copy(localPath, dest, overwrite: true);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                {
                    AppLog.Info($"已保存 {MediaRelative(subDir)}/{displayName} ({len} bytes)");
                    return dest;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("FlyDl", "copy physical: " + ex.Message);
            }
        }

        AppLog.Warn($"写入下载/XuanNiao/{subDir}/{displayName} 失败");
        return null;
    }

    public static bool PublishText(string text, string subDir, string displayName)
    {
        var body = string.IsNullOrWhiteSpace(text) ? "(无文字)" : text;
        var bytes = Encoding.UTF8.GetBytes(body);

        // MediaStore 优先
        if (PublishBytesViaMediaStore(bytes, subDir, displayName, "text/plain"))
            return true;

        var physicalDir = TryEnsureSubdir(subDir);
        if (!string.IsNullOrEmpty(physicalDir))
        {
            try
            {
                File.WriteAllText(Path.Combine(physicalDir, displayName), body, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("FlyDl", "write txt physical: " + ex.Message);
            }
        }

        return false;
    }

    private static string? PublishViaMediaStore(string localPath, string subDir, string displayName, string mimeType)
    {
        try
        {
            var bytes = File.ReadAllBytes(localPath);
            if (bytes.Length == 0) return null;
            if (!PublishBytesViaMediaStore(bytes, subDir, displayName, mimeType))
                return null;
            AppLog.Info($"MediaStore 已写入 {MediaRelative(subDir)}/{displayName} ({bytes.Length} bytes)");
            return $"{MediaRelative(subDir)}/{displayName}";
        }
        catch (Exception ex)
        {
            Log.Warn("FlyDl", "publish read: " + ex.Message);
            return null;
        }
    }

    private static bool PublishBytesViaMediaStore(byte[] bytes, string subDir, string displayName, string mimeType)
    {
        var ctx = global::Android.App.Application.Context;
        var resolver = ctx?.ContentResolver;
        if (resolver == null) return false;

        var relative = MediaRelative(subDir);
        var isImage = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        // 先 Downloads（用户在「下载/XuanNiao」可见）；图片再试 Images 集合作兜底
        if (TryInsertMediaStore(resolver, MediaStore.Downloads.ExternalContentUri, bytes, displayName, mimeType, relative))
            return true;

        if (isImage
            && TryInsertMediaStore(resolver, MediaStore.Images.Media.ExternalContentUri, bytes, displayName, mimeType, relative))
            return true;

        return false;
    }

    private static bool TryInsertMediaStore(
        ContentResolver resolver,
        MediaUri collection,
        byte[] bytes,
        string displayName,
        string mimeType,
        string relativePath)
    {
        MediaUri? uri = null;
        try
        {
            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
            values.Put(MediaStore.IMediaColumns.MimeType, mimeType);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                values.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
                values.Put(MediaStore.IMediaColumns.IsPending, 1);
            }

            uri = resolver.Insert(collection, values);
            if (uri == null)
            {
                Log.Warn("FlyDl", $"MediaStore Insert null collection={collection} mime={mimeType}");
                return false;
            }

            using (var output = resolver.OpenOutputStream(uri))
            {
                if (output == null)
                {
                    try { resolver.Delete(uri, null, null); } catch { /* ignore */ }
                    return false;
                }

                output.Write(bytes, 0, bytes.Length);
                output.Flush();
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                var done = new ContentValues();
                done.Put(MediaStore.IMediaColumns.IsPending, 0);
                resolver.Update(uri, done, null, null);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("FlyDl", $"MediaStore {collection}: {ex.Message}");
            AppLog.Warn("MediaStore 写入失败: " + ex.Message);
            if (uri != null)
            {
                try { resolver.Delete(uri, null, null); } catch { /* ignore */ }
            }
            return false;
        }
    }
}
#endif
