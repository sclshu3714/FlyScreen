using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FlyScreen.Setup.Models;

namespace FlyScreen.Setup.Services;

public sealed class PublishService
{
    private readonly ProcessRunner _runner = new();
    private readonly string _repoRoot;

    /// <summary>当前任务工作区（artifacts/setup-work/{jobId}）；空表示未隔离。</summary>
    private string _workRoot = "";
    private string _jobId = "";
    private IReadOnlyDictionary<string, string>? _jobEnv;

    public PublishService(string repoRoot) => _repoRoot = repoRoot;

    public async Task PublishAsync(
        IReadOnlyList<PublishTargetItem> targets,
        PublishOptions options,
        Action<string> log,
        IProgress<PublishProgress>? progress = null,
        CancellationToken ct = default,
        string? jobId = null)
    {
        if (targets.Count == 0)
            throw new InvalidOperationException("请至少选择一个打包目标。");

        BeginJobWorkspace(jobId, log);
        try
        {
            Report(progress, 0, "准备资源…");
            AssetGenerator.EnsurePublisherAssets(
                Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets"),
                log);

            Directory.CreateDirectory(options.OutputRoot);

            var total = Math.Max(1, targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                ct.ThrowIfCancellationRequested();
                var from = i * 100.0 / total;
                var to = (i + 1) * 100.0 / total;
                var slice = new RangeProgress(progress, from, to);

                target.ApplySelectedFrameworkRuntime();
                log($"======== 开始打包: {target.DisplayName} ({target.Rid}" +
                    (string.IsNullOrWhiteSpace(target.Framework) ? "" : $", {target.Framework}") +
                    ") ========");
                Report(slice, 0, $"[{i + 1}/{total}] {target.DisplayName}");

                if (target.RequiresMac && !OperatingSystem.IsMacOS())
                {
                    log($"跳过 {target.DisplayName}：需要在 macOS 上构建。");
                    Report(slice, 100, $"跳过 {target.DisplayName}");
                    continue;
                }

                if (options.PackageMode == PackageMode.Installer)
                {
                    if (!target.SupportsInstaller)
                    {
                        log($"跳过 {target.DisplayName}：不支持安装包模式。");
                        Report(slice, 100, $"跳过 {target.DisplayName}");
                        continue;
                    }

                    if (target.UsesWindowsSetup)
                    {
                        await PublishInstallerAsync(target, options, log, slice, ct).ConfigureAwait(false);
                    }
                    else if (target.UsesArchiveInstaller)
                    {
                        log($"{target.DisplayName} → 安装包产物（tar.gz，含字体与许可）。");
                        await PublishStandaloneAsync(target, options, log, slice, ct, archiveInstaller: true)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        log($"{target.DisplayName} → 安装包产物（APK）。");
                        await PublishStandaloneAsync(target, options, log, slice, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (!target.SupportsStandalone)
                    {
                        log($"跳过 {target.DisplayName}：须使用「安装包」模式（如 Android APK）。");
                        Report(slice, 100, $"跳过 {target.DisplayName}");
                        continue;
                    }

                    await PublishStandaloneAsync(target, options, log, slice, ct).ConfigureAwait(false);
                }

                Report(slice, 100, $"{target.DisplayName} 完成");
            }

            Report(progress, 100, "全部任务完成");
            log("全部任务完成。");
        }
        finally
        {
            CleanupJobWorkspace(log);
        }
    }

    private void BeginJobWorkspace(string? jobId, Action<string> log)
    {
        _jobId = string.IsNullOrWhiteSpace(jobId)
            ? Guid.NewGuid().ToString("N")
            : jobId.Trim();
        _workRoot = Path.Combine(_repoRoot, "artifacts", "setup-work", _jobId);
        PrepareCleanDir(_workRoot);

        _jobEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FlyScreenJobId"] = _jobId,
            // 避免 MSBuild 节点复用跨任务抢锁
            ["MSBUILDDISABLENODEREUSE"] = "1"
        };

        log($"任务工作区: {_workRoot}");
    }

    private void CleanupJobWorkspace(Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(_workRoot) || !Directory.Exists(_workRoot))
            return;

        try
        {
            Directory.Delete(_workRoot, recursive: true);
            log($"已清理中间文件: {_workRoot}");
        }
        catch (Exception ex)
        {
            log($"清理中间文件失败（可稍后手动删除）: {_workRoot} — {ex.Message}");
        }
        finally
        {
            _workRoot = "";
            _jobId = "";
            _jobEnv = null;
        }
    }

    private string JobPath(params string[] parts)
    {
        var segs = new string[parts.Length + 1];
        segs[0] = _workRoot;
        Array.Copy(parts, 0, segs, 1, parts.Length);
        return Path.Combine(segs);
    }

    private async Task PublishStandaloneAsync(
        PublishTargetItem target,
        PublishOptions options,
        Action<string> log,
        IProgress<PublishProgress>? progress,
        CancellationToken ct,
        bool archiveInstaller = false)
    {
        var staging = JobPath("app");
        PrepareCleanDir(staging);

        EnsureReleaseConfiguration(options, log);

        Report(progress, 8, $"{target.DisplayName}：dotnet publish…");
        var exit = await RunDotnetPublishAsync(target, options, staging, singleFile: true, log, ct)
            .ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"dotnet publish 失败 ({target.Id})，退出码 {exit}");

        Report(progress, 72, $"{target.DisplayName}：清理调试符号…");
        StripDebugSymbols(staging, log);

        Report(progress, 74, $"{target.DisplayName}：工具仅保留 Bundled（未安装）…");
        StripToolsInstallStateFromDir(staging, log);

        if (target.Id == "maui-android")
        {
            Report(progress, 78, $"{target.DisplayName}：签名 APK…");
            var apkScript = Path.Combine(_repoRoot, "build", "Ensure-MauiSignedApk.ps1");
            if (File.Exists(apkScript))
            {
                var androidTfm = string.IsNullOrWhiteSpace(target.Framework)
                    ? "net10.0-android"
                    : target.Framework;
                // 与 Directory.Build.props 中 BaseIntermediateOutputPath 对齐
                var intermediateDir = Path.Combine(
                    "artifacts", "setup-work", _jobId, "obj", "FlyScreen.App.Maui",
                    "Release", androidTfm, "android", "bin");
                var args =
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{apkScript}\" " +
                    $"-OutDir \"{staging}\" -PackageId \"com.xuanniao.flyscreen\" " +
                    $"-IntermediateDir \"{intermediateDir}\"";
                exit = await _runner.RunAsync("powershell", args, _repoRoot, log, ct, _jobEnv)
                    .ConfigureAwait(false);
                if (exit != 0)
                    throw new InvalidOperationException("Ensure-MauiSignedApk 失败");
            }
        }

        Report(progress, 88, $"{target.DisplayName}：整理产物…");
        var finish = Path.Combine(_repoRoot, "build", "Finish-PublishToArtifacts.ps1");
        var kind = archiveInstaller ? "UnixArchive" : target.Kind;
        var finishArgs =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{finish}\" " +
            $"-SourceDir \"{staging}\" -Rid \"{target.Rid}\" -Kind {kind} " +
            $"-RepoRoot \"{_repoRoot}\" -OutDir \"{options.OutputRoot}\"";
        exit = await _runner.RunAsync("powershell", finishArgs, _repoRoot, log, ct, _jobEnv)
            .ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"Finish-PublishToArtifacts 失败 ({target.Id})");
    }

    private async Task PublishInstallerAsync(
        PublishTargetItem target,
        PublishOptions options,
        Action<string> log,
        IProgress<PublishProgress>? progress,
        CancellationToken ct)
    {
        var installerDir = RepoPaths.InstallerDir(_repoRoot);
        AssetGenerator.EnsureInstallerProject(installerDir, log);

        var carouselSource = string.IsNullOrWhiteSpace(options.CarouselDir)
            ? Path.Combine(_repoRoot, "src", "FlyScreen.Assets", "Installer")
            : options.CarouselDir;
        var licenseSource = string.IsNullOrWhiteSpace(options.LicenseFile)
            ? Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets", "license.txt")
            : options.LicenseFile;

        Report(progress, 5, $"{target.DisplayName}：准备安装资源…");
        // 轮播/许可仍同步到安装工程 Assets（只读资源）；payload/清单写入本任务工作区
        AssetGenerator.EnsureInstallerAssets(installerDir, carouselSource, licenseSource, log);

        EnsureReleaseConfiguration(options, log);

        Report(progress, 12, $"{target.DisplayName}：发布应用…");
        var appStaging = JobPath("app");
        PrepareCleanDir(appStaging);
        var exit = await RunDotnetPublishAsync(target, options, appStaging, singleFile: false, log, ct)
            .ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"dotnet publish 失败 ({target.Id})");

        Report(progress, 48, $"{target.DisplayName}：清理调试符号…");
        StripDebugSymbols(appStaging, log);

        Report(progress, 50, $"{target.DisplayName}：工具仅保留 Bundled（未安装）…");
        StripToolsInstallStateFromDir(appStaging, log);

        Report(progress, 52, $"{target.DisplayName}：发布卸载器…");
        await PublishUninstallerIntoAsync(appStaging, options, log, ct).ConfigureAwait(false);

        Report(progress, 58, $"{target.DisplayName}：打包 payload…");
        var payloadZip = JobPath("payload.zip");
        if (File.Exists(payloadZip)) File.Delete(payloadZip);
        log($"打包 payload: {payloadZip}");
        ZipFile.CreateFromDirectory(appStaging, payloadZip, CompressionLevel.Optimal, includeBaseDirectory: false);

        var requiredBytes = new FileInfo(payloadZip).Length + 32L * 1024 * 1024;
        var version = ReadVersion();

        Report(progress, 68, $"{target.DisplayName}：写入安装清单…");
        var carouselFiles = Directory.GetFiles(Path.Combine(installerDir, "Assets", "carousel"), "*.png")
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var manifest = new InstallerManifest
        {
            ProductName = options.ProductName,
            PublisherName = options.PublisherName,
            Version = version,
            ExeName = ResolveExeName(appStaging, options.ExeName),
            DefaultInstallDir = options.DefaultInstallDir,
            LicenseUrl = options.LicenseUrl,
            RequiredBytes = requiredBytes,
            CreateDesktopShortcut = options.CreateDesktopShortcut,
            CreateStartMenuShortcut = options.CreateStartMenuShortcut,
            CarouselImages = carouselFiles
        };

        var manifestPath = JobPath("installer.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        log($"已写入安装清单: {manifestPath}");

        Report(progress, 72, $"{target.DisplayName}：发布安装程序…");
        var setupOut = JobPath("setup");
        PrepareCleanDir(setupOut);

        var installerObj = JobPath("obj", "FlyScreen.Installer");
        Directory.CreateDirectory(installerObj);

        var sc = options.DeploymentMode == DeploymentMode.SelfContained ? "true" : "false";
        var publishArgs =
            $"publish \"{Path.Combine(installerDir, "FlyScreen.Installer.csproj")}\" " +
            $"-c Release -r win-x64 --self-contained {sc} " +
            $"-p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true " +
            $"-p:DebugType=None -p:DebugSymbols=false " +
            $"-p:EnableCompressionInSingleFile=true " +
            $"-p:FlyScreenPayloadZip=\"{payloadZip}\" " +
            $"-p:FlyScreenInstallerJson=\"{manifestPath}\" " +
            $"-p:BaseIntermediateOutputPath={QuoteMsBuildDir(installerObj)} " +
            $"-o \"{setupOut}\"";

        exit = await _runner.RunAsync("dotnet", publishArgs, _repoRoot, log, ct, _jobEnv)
            .ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException("安装程序 publish 失败");

        StripDebugSymbols(setupOut, log);

        Report(progress, 92, $"{target.DisplayName}：输出 Setup…");
        var setupExe = Directory.EnumerateFiles(setupOut, "*.exe")
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("未找到安装程序 exe");

        Directory.CreateDirectory(options.OutputRoot);
        var finalName = $"FlyScreen-{version}-{target.Rid}-Setup.exe";
        var finalPath = Path.Combine(options.OutputRoot, finalName);
        File.Copy(setupExe, finalPath, overwrite: true);
        log($"安装包已输出: {finalPath}");
    }

    private static string TrimSlash(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 供 -p:Dir= 使用。Windows 上 <c>"D:\path\"</c> 末尾反斜杠会转义结束引号，
    /// 把后面的 <c>-o ...</c> 吞进属性值（NuGet「文件名语法不正确」）。改用正斜杠结尾。
    /// </summary>
    private static string QuoteMsBuildDir(string path)
        => "\"" + TrimSlash(path).Replace('\\', '/') + "/\"";

    private static void Report(IProgress<PublishProgress>? progress, double percent, string message)
        => progress?.Report(PublishProgress.Create(percent, message));

    private sealed class RangeProgress(IProgress<PublishProgress>? inner, double from, double to)
        : IProgress<PublishProgress>
    {
        public void Report(PublishProgress value)
        {
            if (inner == null) return;
            var mapped = from + Math.Clamp(value.Percent, 0, 100) / 100.0 * (to - from);
            inner.Report(PublishProgress.Create(mapped, value.Message));
        }
    }

    private async Task<int> RunDotnetPublishAsync(
        PublishTargetItem target,
        PublishOptions options,
        string outputDir,
        bool singleFile,
        Action<string> log,
        CancellationToken ct)
    {
        var project = Path.Combine(_repoRoot, target.ProjectPath);
        var selfContained = target.SupportsSelfContained
            && options.DeploymentMode == DeploymentMode.SelfContained;

        // 强制 Release + 不生成调试符号
        var configuration = string.Equals(options.Configuration, "Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Release";

        var args = new List<string>
        {
            "publish",
            $"\"{project}\"",
            $"-c {configuration}",
            $"-o \"{outputDir}\"",
            "-p:DebugType=None",
            "-p:DebugSymbols=false"
        };

        if (!string.IsNullOrWhiteSpace(target.Framework))
            args.Add($"-f {target.Framework}");

        // Android 不使用 -r；Maui Win / Maui Mac 的 RID 在 ExtraArgs / Framework 中处理
        if (target.Id is "win" or "linux" or "mac")
            args.Add($"-r {target.Rid}");

        if (target.SupportsSelfContained)
            args.Add(selfContained ? "--self-contained true" : "--self-contained false");

        if (singleFile && target.Kind != "Apk")
        {
            args.Add("-p:PublishSingleFile=true");
            args.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
            args.Add("-p:EnableCompressionInSingleFile=true");
        }
        else if (!singleFile && target.Kind != "Apk")
        {
            args.Add("-p:PublishSingleFile=false");
        }

        if (!string.IsNullOrWhiteSpace(target.ExtraArgs))
            args.Add(target.ExtraArgs);

        // 仅 Windows 电脑端可打入 VddOffline；Mac / Linux / 手机端忽略该选项
        if (target.SupportsVddOffline)
        {
            if (options.IncludeVddOffline)
            {
                args.Add("-p:FlyScreenSkipVddOffline=false");
                log("包含虚拟显示离线包 VddOffline\\FlyScreen.VddOffline.zip");
            }
            else
            {
                args.Add("-p:FlyScreenSkipVddOffline=true");
                log("已跳过虚拟显示离线包（使用时走 winget/在线安装）");
            }
        }

        log($"dotnet publish → {target.Id} ({configuration}, 无 PDB, job={_jobId})");
        var exit = await _runner.RunAsync("dotnet", string.Join(' ', args), _repoRoot, log, ct, _jobEnv)
            .ConfigureAwait(false);
        if (exit == 0 && target.SupportsVddOffline && !options.IncludeVddOffline)
            StripVddOfflineFromDir(outputDir, log);
        return exit;
    }

    private static void StripVddOfflineFromDir(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) return;
        var removed = 0;
        foreach (var zip in Directory.EnumerateFiles(dir, "FlyScreen.VddOffline.zip", SearchOption.AllDirectories))
        {
            try { File.Delete(zip); removed++; } catch { /* ignore */ }
        }

        foreach (var folder in Directory.EnumerateDirectories(dir, "VddOffline", SearchOption.AllDirectories))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                removed++;
            }
            catch { /* ignore */ }
        }

        if (removed > 0)
            log($"已从产物中移除 VddOffline 离线包（{removed} 项）。");
    }

    /// <summary>
    /// 安装包/发布产物只携带 <c>Tools/Bundled/*.flytool</c>，不预装、不带安装记录。
    /// 避免开发机旁路 <c>Installed</c>/<c>tools-install.json</c> 被打进 payload，导致安装后显示「已安装」却不可用。
    /// </summary>
    private static void StripToolsInstallStateFromDir(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) return;
        var removed = 0;

        foreach (var toolsRoot in Directory.EnumerateDirectories(dir, "Tools", SearchOption.AllDirectories))
        {
            try
            {
                var installed = Path.Combine(toolsRoot, "Installed");
                if (Directory.Exists(installed))
                {
                    Directory.Delete(installed, recursive: true);
                    removed++;
                }

                // 保留空 Installed 目录结构也可；此处不重建，运行时 EnsureToolsLayout 会建

                foreach (var name in new[]
                         {
                             "tools-install.json",
                             "tools-shortcuts.json",
                             "tools-local-programs.json",
                             "tools-index.cache.json"
                         })
                {
                    var f = Path.Combine(toolsRoot, name);
                    if (!File.Exists(f)) continue;
                    File.Delete(f);
                    removed++;
                }

                // 非 Bundled 的杂项目录一律去掉（Cache 等遗留）
                foreach (var sub in Directory.EnumerateDirectories(toolsRoot))
                {
                    var leaf = Path.GetFileName(sub);
                    if (leaf.Equals("Bundled", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        Directory.Delete(sub, recursive: true);
                        removed++;
                    }
                    catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                log($"清理 Tools 安装态失败: {toolsRoot} — {ex.Message}");
            }
        }

        if (removed > 0)
            log($"已从产物剥离工具安装态（{removed} 项），仅保留 Tools/Bundled 供安装后手动安装。");
        else
            log("工具目录已是未安装态（仅 Bundled / 无安装记录）。");
    }

    private static void EnsureReleaseConfiguration(PublishOptions options, Action<string> log)
    {
        if (!string.Equals(options.Configuration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            log($"配置已从 {options.Configuration} 纠正为 Release。");
            options.Configuration = "Release";
        }
    }

    private static void StripDebugSymbols(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) return;
        var pdbs = Directory.GetFiles(dir, "*.pdb", SearchOption.AllDirectories);
        foreach (var pdb in pdbs)
        {
            try { File.Delete(pdb); }
            catch { /* ignore */ }
        }
        if (pdbs.Length > 0)
            log($"已移除 {pdbs.Length} 个 .pdb 调试符号文件。");
    }

    private async Task PublishUninstallerIntoAsync(
        string appStaging,
        PublishOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        var uninstallerProj = Path.Combine(RepoPaths.UninstallerDir(_repoRoot), "FlyScreen.Uninstaller.csproj");
        if (!File.Exists(uninstallerProj))
        {
            log("警告: 未找到卸载工程，安装包将不含 Uninstall.exe");
            return;
        }

        var unOut = JobPath("uninstaller");
        PrepareCleanDir(unOut);

        EnsureReleaseConfiguration(options, log);
        var sc = options.DeploymentMode == DeploymentMode.SelfContained ? "true" : "false";
        var args =
            $"publish \"{uninstallerProj}\" -c Release -r win-x64 " +
            $"--self-contained {sc} -p:PublishSingleFile=true " +
            $"-p:DebugType=None -p:DebugSymbols=false " +
            $"-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true " +
            $"-o \"{unOut}\"";

        log("发布卸载程序 Uninstall.exe（Release）…");
        var exit = await _runner.RunAsync("dotnet", args, _repoRoot, log, ct, _jobEnv).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException("卸载程序 publish 失败");

        StripDebugSymbols(unOut, log);

        var exe = Directory.EnumerateFiles(unOut, "Uninstall.exe")
            .FirstOrDefault()
            ?? Directory.EnumerateFiles(unOut, "*.exe").FirstOrDefault();
        if (exe == null)
            throw new InvalidOperationException("未找到 Uninstall.exe");

        var dest = Path.Combine(appStaging, "Uninstall.exe");
        File.Copy(exe, dest, overwrite: true);
        log($"已纳入卸载程序: {dest}");
    }

    private string ReadVersion()
    {
        try
        {
            var json = File.ReadAllText(RepoPaths.VersionJson(_repoRoot));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var v))
                return v.GetString() ?? "1.0.0";
        }
        catch
        {
            // ignore
        }
        return "1.0.0";
    }

    private static string ResolveExeName(string appDir, string preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var p = Path.Combine(appDir, preferred);
            if (File.Exists(p)) return preferred;
        }

        var hit = Directory.EnumerateFiles(appDir, "*.exe")
            .Select(Path.GetFileName)
            .FirstOrDefault(n => n != null && n.Contains("FlyScreen", StringComparison.OrdinalIgnoreCase));
        return hit ?? preferred ?? "FlyScreen.exe";
    }

    private static void PrepareCleanDir(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }
}
