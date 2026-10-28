namespace FlyScreen.Services;

/// <summary>
/// 本机数据根目录。默认 <c>{ApplicationData}/FlyScreen</c>；
/// Android 启动时 <see cref="SetRoot"/> 为
/// <c>…/Android/data/com.xuanniao.flyscreen/files</c>（无需所有文件访问，卸载随应用清理）。
/// </summary>
public static class FlyScreenDataPaths
{
    private static string? _rootOverride;
    private static string? _inboxOverride;

    /// <summary>数据根（日志 / 程序 / 设置等）。收件箱可单独覆盖到公共下载目录。</summary>
    public static string Root
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_rootOverride))
                return _rootOverride!;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
                return Path.Combine(appData, "FlyScreen");

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".flyscreen");
        }
    }

    /// <summary>是否已由宿主覆盖根目录。</summary>
    public static bool HasOverride => !string.IsNullOrWhiteSpace(_rootOverride);

    public static string LogsDirectory => Path.Combine(Root, "Logs");
    public static string ProgramsDirectory => Path.Combine(Root, "Programs");
    public static string InboxDirectory
        => !string.IsNullOrWhiteSpace(_inboxOverride)
            ? _inboxOverride!
            : Path.Combine(Root, "Inbox");
    public static string PasteStagingDirectory => Path.Combine(Root, "PasteStaging");
    public static string OcrSamplesDirectory => Path.Combine(Root, "ocr_samples");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string TransferHistoryFile => Path.Combine(Root, "transfer-history.json");

    /// <summary>
    /// 覆盖数据根。传 null 恢复默认。成功时会创建目录。
    /// </summary>
    public static void SetRoot(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            _rootOverride = null;
            return;
        }

        var path = absolutePath.Trim();
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "Logs"));
        Directory.CreateDirectory(Path.Combine(path, "Programs"));
        Directory.CreateDirectory(Path.Combine(path, "PasteStaging"));
        // Inbox：若未单独覆盖，仍在 Root/Inbox
        if (string.IsNullOrWhiteSpace(_inboxOverride))
            Directory.CreateDirectory(Path.Combine(path, "Inbox"));
        Directory.CreateDirectory(Path.Combine(path, "ocr_samples"));
        _rootOverride = path;
    }

    /// <summary>
    /// 单独指定收件箱（如 Android <c>Download/XuanNiao/Inbox</c>，便于文件管理直接查看）。
    /// </summary>
    public static void SetInboxDirectory(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            _inboxOverride = null;
            return;
        }

        var path = absolutePath.Trim();
        Directory.CreateDirectory(path);
        _inboxOverride = path;
    }

    /// <summary>
    /// 若新根尚无 <c>settings.json</c>，而旧的 ApplicationData/FlyScreen 有数据，则拷贝过去（仅一次迁移）。
    /// </summary>
    public static void TryMigrateFromLegacyAppData()
    {
        if (string.IsNullOrWhiteSpace(_rootOverride))
            return;

        try
        {
            if (File.Exists(SettingsFile))
                return;

            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FlyScreen");
            if (string.IsNullOrWhiteSpace(legacy)
                || string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(_rootOverride), StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(legacy))
                return;

            CopyDirectoryRecursive(legacy, _rootOverride);
        }
        catch
        {
            // 迁移失败不阻断启动；新目录从空开始
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(destDir, name);
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
