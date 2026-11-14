namespace FlyScreen.Services;

/// <summary>
/// 本机数据根目录。默认 <c>{ApplicationData}/FlyScreen</c>；
/// Android 启动时 <see cref="SetRoot"/> 为
/// <c>…/Android/data/com.xuanniao.flyscreen/files</c>（无需所有文件访问，卸载随应用清理）。
/// 工具插件目录默认在程序旁 <c>BaseDirectory/Tools</c>，不写入 Roaming。
/// </summary>
public static class FlyScreenDataPaths
{
    private static string? _rootOverride;
    private static string? _inboxOverride;
    private static string? _toolsRootOverride;
    private static bool _toolsMigrated;

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

    /// <summary>
    /// 工具根目录。桌面默认 <c>{BaseDirectory}/Tools</c>；
    /// 当 BaseDirectory 不可写（或已 SetRoot 的移动端）时回落到 <c>{Root}/Tools</c>。
    /// </summary>
    public static string ToolsRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_toolsRootOverride))
                return _toolsRootOverride!;

            // 已 SetRoot（典型 Android）：工具跟应用私有 files，仍非 Roaming
            if (!string.IsNullOrWhiteSpace(_rootOverride))
                return Path.Combine(_rootOverride!, "Tools");

            var beside = Path.Combine(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "Tools");
            if (CanWriteDirectory(beside))
                return beside;

            return Path.Combine(Root, "Tools");
        }
    }

    public static string LogsDirectory => Path.Combine(Root, "Logs");
    public static string ProgramsDirectory => Path.Combine(Root, "Programs");

    /// <summary>已安装工具目录：Tools/Installed/&lt;id&gt;/&lt;version&gt;/</summary>
    public static string ToolsDirectory => ToolsInstalledDirectory;

    /// <summary>工具源包目录：内置 + 导入/远程下载的 .flytool（不再单独设 Cache）。</summary>
    public static string ToolsBundledDirectory => Path.Combine(ToolsRoot, "Bundled");

    /// <summary>已废弃：与 <see cref="ToolsBundledDirectory"/> 同一路径，仅兼容旧调用。</summary>
    [Obsolete("已合并到 ToolsBundledDirectory")]
    public static string ToolsCacheDirectory => ToolsBundledDirectory;

    public static string ToolsInstalledDirectory => Path.Combine(ToolsRoot, "Installed");

    /// <summary>工具安装记录（不进 BehaviorSync；位于 Tools 根下）。</summary>
    public static string ToolsInstallStateFile => Path.Combine(ToolsRoot, "tools-install.json");

    /// <summary>工具页快捷栏槽位。</summary>
    public static string ToolsShortcutsFile => Path.Combine(ToolsRoot, "tools-shortcuts.json");

    /// <summary>工具页本地运行程序登记（无安装态）。</summary>
    public static string ToolsLocalProgramsFile => Path.Combine(ToolsRoot, "tools-local-programs.json");

    /// <summary>远程 tools-index 本地缓存。</summary>
    public static string ToolsIndexCacheFile => Path.Combine(ToolsRoot, "tools-index.cache.json");

    /// <summary>共享设备「记住适配」记录。</summary>
    public static string SharedDevicesRememberFile => Path.Combine(Root, "shared-devices-remember.json");
    /// <summary>共享设备提供方本机选用（扬声器/麦克风/摄像头 Id）。</summary>
    public static string SharedDevicesSelectionFile => Path.Combine(Root, "shared-devices-selection.json");
    public static string InboxDirectory
        => !string.IsNullOrWhiteSpace(_inboxOverride)
            ? _inboxOverride!
            : Path.Combine(Root, "Inbox");
    public static string PasteStagingDirectory => Path.Combine(Root, "PasteStaging");
    public static string OcrSamplesDirectory => Path.Combine(Root, "ocr_samples");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string TransferHistoryFile => Path.Combine(Root, "transfer-history.json");

    /// <summary>工具专属数据目录：{Root}/ToolData/{toolId}/</summary>
    public static string GetToolDataDirectory(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
            throw new ArgumentException("toolId 不能为空", nameof(toolId));
        var dir = Path.Combine(Root, "ToolData", toolId.Trim());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>覆盖工具根。传 null 恢复默认。</summary>
    public static void SetToolsRoot(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            _toolsRootOverride = null;
            return;
        }

        var path = absolutePath.Trim();
        EnsureToolsTree(path);
        _toolsRootOverride = path;
    }

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
        EnsureToolsTree(Path.Combine(path, "Tools"));
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

    /// <summary>确保 Tools 子树存在；并尝试从 Roaming 旧路径迁移一次。</summary>
    public static void EnsureToolsLayout()
    {
        EnsureToolsTree(ToolsRoot);
        TryMigrateLegacyToolsFromAppData();
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

    private static void TryMigrateLegacyToolsFromAppData()
    {
        if (_toolsMigrated) return;
        _toolsMigrated = true;

        try
        {
            var legacyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FlyScreen");
            var legacyTools = Path.Combine(legacyRoot, "Tools");
            var legacyState = Path.Combine(legacyRoot, "tools-install.json");
            var destRoot = ToolsRoot;

            if (string.Equals(
                    Path.GetFullPath(legacyTools),
                    Path.GetFullPath(destRoot),
                    StringComparison.OrdinalIgnoreCase))
                return;

            var destState = ToolsInstallStateFile;
            if (!File.Exists(destState) && File.Exists(legacyState))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destState)!);
                File.Copy(legacyState, destState, overwrite: false);
            }

            if (Directory.Exists(legacyTools))
            {
                // 旧布局：Tools/<id>/<ver>/  → 新：Installed/<id>/<ver>/
                foreach (var idDir in Directory.EnumerateDirectories(legacyTools))
                {
                    var id = Path.GetFileName(idDir);
                    if (string.IsNullOrEmpty(id)
                        || id.Equals("Bundled", StringComparison.OrdinalIgnoreCase)
                        || id.Equals("Cache", StringComparison.OrdinalIgnoreCase)
                        || id.Equals("Installed", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var verDir in Directory.EnumerateDirectories(idDir))
                    {
                        var ver = Path.GetFileName(verDir);
                        if (string.IsNullOrEmpty(ver)) continue;
                        var dest = Path.Combine(ToolsInstalledDirectory, id, ver);
                        if (Directory.Exists(dest)) continue;
                        CopyDirectoryRecursive(verDir, dest);
                    }
                }
            }

            // 修正安装记录 Path 指向新目录
            if (File.Exists(destState))
            {
                var text = File.ReadAllText(destState);
                if (text.Contains(legacyTools.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    || text.Contains(legacyTools, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Replace(legacyTools.Replace('\\', '/'), ToolsInstalledDirectory.Replace('\\', '/'));
                    text = text.Replace(legacyTools, ToolsInstalledDirectory);
                    // 旧 Path 可能是 Tools/id/ver，新为 Installed/id/ver —— 若仍含 /Tools/ 且不含 Installed，再兜底
                    File.WriteAllText(destState, text);
                }
            }
        }
        catch
        {
            // 迁移失败不阻断
        }
    }

    private static void EnsureToolsTree(string toolsRoot)
    {
        Directory.CreateDirectory(toolsRoot);
        Directory.CreateDirectory(Path.Combine(toolsRoot, "Bundled"));
        Directory.CreateDirectory(Path.Combine(toolsRoot, "Installed"));
        TryMergeLegacyToolsCache(toolsRoot);
    }

    /// <summary>旧版 Tools/Cache/*.flytool 并入 Bundled 后删除 Cache 目录。</summary>
    private static void TryMergeLegacyToolsCache(string toolsRoot)
    {
        try
        {
            var cache = Path.Combine(toolsRoot, "Cache");
            if (!Directory.Exists(cache)) return;

            var bundled = Path.Combine(toolsRoot, "Bundled");
            Directory.CreateDirectory(bundled);
            foreach (var file in Directory.EnumerateFiles(cache, "*.flytool", SearchOption.TopDirectoryOnly))
            {
                var dest = Path.Combine(bundled, Path.GetFileName(file));
                if (!File.Exists(dest))
                    File.Copy(file, dest, overwrite: false);
            }

            try { Directory.Delete(cache, recursive: true); }
            catch
            {
                // 若删不净，至少不再依赖该目录
            }
        }
        catch
        {
            // 迁移失败不阻断
        }
    }

    private static bool CanWriteDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
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
