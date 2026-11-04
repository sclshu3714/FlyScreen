using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FlyScreen.Setup.Models;

namespace FlyScreen.Setup.Services;

public sealed class PublishService
{
    private readonly ProcessRunner _runner = new();
    private readonly string _repoRoot;

    public PublishService(string repoRoot) => _repoRoot = repoRoot;

    public async Task PublishAsync(
        IReadOnlyList<PublishTargetItem> targets,
        PublishOptions options,
        Action<string> log,
        CancellationToken ct = default)
    {
        if (targets.Count == 0)
            throw new InvalidOperationException("请至少选择一个打包目标。");

        AssetGenerator.EnsurePublisherAssets(
            Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets"),
            log);

        Directory.CreateDirectory(options.OutputRoot);

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            log($"======== 开始打包: {target.DisplayName} ({target.Rid}) ========");

            if (target.RequiresMac && !OperatingSystem.IsMacOS())
            {
                log($"跳过 {target.DisplayName}：需要在 macOS 上构建。");
                continue;
            }

            if (options.PackageMode == PackageMode.Installer)
            {
                if (!target.SupportsInstaller)
                {
                    log($"跳过 {target.DisplayName}：不支持安装包模式。");
                    continue;
                }

                // Android APK 等：安装包但不走 Windows Setup
                if (!target.UsesWindowsSetup)
                {
                    log($"{target.DisplayName} → 安装包产物（APK）。");
                    await PublishStandaloneAsync(target, options, log, ct).ConfigureAwait(false);
                }
                else
                {
                    await PublishInstallerAsync(target, options, log, ct).ConfigureAwait(false);
                }
            }
            else
            {
                if (!target.SupportsStandalone)
                {
                    log($"跳过 {target.DisplayName}：须使用「安装包」模式（如 Android APK）。");
                    continue;
                }

                await PublishStandaloneAsync(target, options, log, ct).ConfigureAwait(false);
            }
        }

        log("全部任务完成。");
    }

    private async Task PublishStandaloneAsync(
        PublishTargetItem target,
        PublishOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        var staging = Path.Combine(_repoRoot, "artifacts", "setup-staging", target.Rid, "app");
        PrepareCleanDir(staging);

        EnsureReleaseConfiguration(options, log);

        var exit = await RunDotnetPublishAsync(target, options, staging, singleFile: true, log, ct)
            .ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"dotnet publish 失败 ({target.Id})，退出码 {exit}");

        StripDebugSymbols(staging, log);

        if (target.Id == "maui-android")
        {
            var apkScript = Path.Combine(_repoRoot, "build", "Ensure-MauiSignedApk.ps1");
            if (File.Exists(apkScript))
            {
                var args =
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{apkScript}\" " +
                    $"-OutDir \"{staging}\" -PackageId \"com.xuanniao.flyscreen\" " +
                    $"-IntermediateDir \"src\\FlyScreen.App.Maui\\obj\\Release\\net10.0-android\\android\\bin\"";
                exit = await _runner.RunAsync("powershell", args, _repoRoot, log, ct).ConfigureAwait(false);
                if (exit != 0)
                    throw new InvalidOperationException("Ensure-MauiSignedApk 失败");
            }
        }

        var finish = Path.Combine(_repoRoot, "build", "Finish-PublishToArtifacts.ps1");
        var kind = target.Kind;
        var finishArgs =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{finish}\" " +
            $"-SourceDir \"{staging}\" -Rid \"{target.Rid}\" -Kind {kind} " +
            $"-RepoRoot \"{_repoRoot}\" -OutDir \"{options.OutputRoot}\"";
        exit = await _runner.RunAsync("powershell", finishArgs, _repoRoot, log, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"Finish-PublishToArtifacts 失败 ({target.Id})");
    }

    private async Task PublishInstallerAsync(
        PublishTargetItem target,
        PublishOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        var installerDir = RepoPaths.InstallerDir(_repoRoot);
        AssetGenerator.EnsureInstallerProject(installerDir, log);

        var carouselSource = string.IsNullOrWhiteSpace(options.CarouselDir)
            ? Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets", "carousel")
            : options.CarouselDir;
        var licenseSource = string.IsNullOrWhiteSpace(options.LicenseFile)
            ? Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets", "license.txt")
            : options.LicenseFile;

        AssetGenerator.EnsureInstallerAssets(installerDir, carouselSource, licenseSource, log);

        EnsureReleaseConfiguration(options, log);

        // 1) 发布应用为目录（Release，各端独立输出；安装包内解压，不强制单文件）
        var appStaging = Path.Combine(_repoRoot, "artifacts", "setup-staging", target.Rid, "app");
        PrepareCleanDir(appStaging);
        var exit = await RunDotnetPublishAsync(target, options, appStaging, singleFile: false, log, ct)
            .ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"dotnet publish 失败 ({target.Id})");

        StripDebugSymbols(appStaging, log);

        // 1b) 发布卸载器并放入应用目录（随 payload 安装）
        await PublishUninstallerIntoAsync(appStaging, options, log, ct).ConfigureAwait(false);

        // 2) 打 payload.zip（已去掉 pdb）
        var payloadDir = Path.Combine(installerDir, "Payload");
        PrepareCleanDir(payloadDir);
        var payloadZip = Path.Combine(payloadDir, "payload.zip");
        log($"打包 payload: {payloadZip}");
        if (File.Exists(payloadZip)) File.Delete(payloadZip);
        ZipFile.CreateFromDirectory(appStaging, payloadZip, CompressionLevel.Optimal, includeBaseDirectory: false);

        var requiredBytes = new FileInfo(payloadZip).Length + 32L * 1024 * 1024;
        var version = ReadVersion();

        // 3) 写安装清单
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
            LaunchAfterInstall = options.LaunchAfterInstall,
            CarouselImages = carouselFiles
        };

        var manifestPath = Path.Combine(installerDir, "Assets", "installer.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        log($"已写入安装清单: {manifestPath}");

        // 4) 发布安装程序（自包含单文件，内含 payload）
        var setupOut = Path.Combine(_repoRoot, "artifacts", "setup-staging", target.Rid, "setup");
        PrepareCleanDir(setupOut);

        var sc = options.DeploymentMode == DeploymentMode.SelfContained ? "true" : "false";
        var publishArgs =
            $"publish \"{Path.Combine(installerDir, "FlyScreen.Installer.csproj")}\" " +
            $"-c Release -r win-x64 --self-contained {sc} " +
            $"-p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true " +
            $"-p:DebugType=None -p:DebugSymbols=false " +
            $"-p:EnableCompressionInSingleFile=true -o \"{setupOut}\"";

        exit = await _runner.RunAsync("dotnet", publishArgs, _repoRoot, log, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException("安装程序 publish 失败");

        StripDebugSymbols(setupOut, log);

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

        // Win / Maui-Win：可选跳过内置 VddOffline zip（运行时走 winget/在线）
        if (target.Id is "win" or "maui-win")
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

        log($"dotnet publish → {target.Id} ({configuration}, 无 PDB)");
        var exit = await _runner.RunAsync("dotnet", string.Join(' ', args), _repoRoot, log, ct)
            .ConfigureAwait(false);
        if (exit == 0 && target.Id is "win" or "maui-win" && !options.IncludeVddOffline)
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

        var unOut = Path.Combine(_repoRoot, "artifacts", "setup-staging", "uninstaller");
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
        var exit = await _runner.RunAsync("dotnet", args, _repoRoot, log, ct).ConfigureAwait(false);
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
