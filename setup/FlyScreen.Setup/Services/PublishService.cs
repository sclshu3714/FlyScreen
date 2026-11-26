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
            // 清掉历史上残留的 RID 暂存 / stage / 检查目录，避免 artifacts 无限膨胀
            CleanTransientArtifactRoots(log);

            Report(progress, 0, "准备资源…");
            AssetGenerator.EnsurePublisherAssets(
                Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets"),
                log);

            Directory.CreateDirectory(options.OutputRoot);
            options.Version = ResolvePublishVersion(options, log);
            log($"打包版本以 Setup 配置为准: v{options.Version}（禁用 Git 自动递增）");

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
                    $", v{options.Version}) ========");
                Report(slice, 0, $"[{i + 1}/{total}] {target.DisplayName}");

                if (target.RequiresMac && !OperatingSystem.IsMacOS())
                {
                    log($"跳过 {target.DisplayName}：需要在 macOS 上构建。");
                    Report(slice, 100, $"跳过 {target.DisplayName}");
                    continue;
                }

                if (target.IsFlyTools)
                {
                    await PublishFlyToolsAsync(target, options, log, slice, ct).ConfigureAwait(false);
                }
                else if (target.IsRelayDocker)
                {
                    await PublishRelayDockerAsync(target, options, log, slice, ct).ConfigureAwait(false);
                }
                else if (target.IsRelay)
                {
                    await PublishRelayAsync(target, options, log, slice, ct).ConfigureAwait(false);
                }
                else if (options.PackageMode == PackageMode.Installer)
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

        // 顺带清掉其它任务残留的 setup-work（含旧版按 RID 命名的目录）
        PurgeOrphanSetupWork(log, keepJobId: _jobId);

        log($"任务工作区: {_workRoot}");
    }

    private void CleanupJobWorkspace(Action<string> log)
    {
        var root = _workRoot;
        try
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                if (TryDeleteDirectory(root, retries: 8))
                    log($"已清理中间文件: {root}");
                else
                    log($"清理中间文件失败（可稍后运行 build/Clean-Artifacts.ps1）: {root}");
            }

            PurgeOrphanSetupWork(log, keepJobId: null);
        }
        finally
        {
            _workRoot = "";
            _jobId = "";
            _jobEnv = null;
        }
    }

    /// <summary>
    /// 删除 artifacts 下非交付物目录/文件。保留 release、flytools，以及当前任务的 setup-work/{jobId}。
    /// </summary>
    private void CleanTransientArtifactRoots(Action<string> log)
    {
        var artifacts = Path.Combine(_repoRoot, "artifacts");
        if (!Directory.Exists(artifacts))
            return;

        var keepDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "release",
            "flytools",
            "setup-work"
        };

        foreach (var dir in Directory.GetDirectories(artifacts))
        {
            var name = Path.GetFileName(dir);
            if (keepDirs.Contains(name))
                continue;

            if (TryDeleteDirectory(dir, retries: 4))
                log($"已清理中间目录: {dir}");
            else
                log($"清理中间目录失败: {dir}");
        }

        foreach (var file in Directory.GetFiles(artifacts))
        {
            if (TryDeleteFile(file, retries: 3))
                log($"已清理中间文件: {file}");
        }
    }

    /// <summary>
    /// 清理 setup-work 下非当前任务的残留（崩溃/锁失败留下的 GUID 目录，以及旧版 linux-x64 等命名目录）。
    /// </summary>
    private void PurgeOrphanSetupWork(Action<string> log, string? keepJobId)
    {
        var root = Path.Combine(_repoRoot, "artifacts", "setup-work");
        if (!Directory.Exists(root))
            return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(keepJobId) &&
                string.Equals(name, keepJobId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryDeleteDirectory(dir, retries: 4))
                log($"已清理残留工作区: {dir}");
            else
                log($"清理残留工作区失败: {dir}");
        }

        // 空 setup-work 可保留；有残留子目录则留给下次再清
    }

    private static bool TryDeleteDirectory(string path, int retries = 5)
    {
        for (var i = 0; i < retries; i++)
        {
            try
            {
                if (!Directory.Exists(path))
                    return true;

                foreach (var info in new DirectoryInfo(path)
                             .EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                {
                    try { info.Attributes = FileAttributes.Normal; }
                    catch { /* ignore */ }
                }

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch
            {
                Thread.Sleep(200 * (i + 1));
            }
        }

        return !Directory.Exists(path);
    }

    private static bool TryDeleteFile(string path, int retries = 3)
    {
        for (var i = 0; i < retries; i++)
        {
            try
            {
                if (!File.Exists(path))
                    return true;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch
            {
                Thread.Sleep(150 * (i + 1));
            }
        }

        return !File.Exists(path);
    }

    private string JobPath(params string[] parts)
    {
        var segs = new string[parts.Length + 1];
        segs[0] = _workRoot;
        Array.Copy(parts, 0, segs, 1, parts.Length);
        return Path.Combine(segs);
    }

    private async Task PublishFlyToolsAsync(
        PublishTargetItem target,
        PublishOptions options,
        Action<string> log,
        IProgress<PublishProgress>? progress,
        CancellationToken ct)
    {
        EnsureReleaseConfiguration(options, log);
        var version = ResolvePublishVersion(options, log);
        var script = Path.Combine(_repoRoot, "build", "Pack-FlyTools.ps1");
        if (!File.Exists(script))
            throw new InvalidOperationException("未找到 build/Pack-FlyTools.ps1");

        var staging = JobPath("flytools");
        PrepareCleanDir(staging);

        var ids = NormalizeSelectedFlyToolIds(target, options, requireSelection: true);
        Report(progress, 10, $"{target.DisplayName}：组包 .flytool（{ids.Count} 个）…");
        await PackFlyToolsToDirAsync(staging, ids, log, ct).ConfigureAwait(false);

        var flytools = Directory.GetFiles(staging, "*.flytool");
        if (flytools.Length == 0)
            throw new InvalidOperationException("未生成任何 .flytool 文件");

        Directory.CreateDirectory(options.OutputRoot);
        Report(progress, 70, $"{target.DisplayName}：输出产物…");

        // 安装包 / 独立应用均交付单文件 zip，便于发版上传；目录副本便于本地调试拷贝
        var zipName = $"FlyScreen-{version}-flytools.zip";
        var zipPath = Path.Combine(options.OutputRoot, zipName);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        log($"插件工具包已输出: {zipPath}（{flytools.Length} 个 .flytool）");

        if (options.PackageMode == PackageMode.Standalone)
        {
            var dirOut = Path.Combine(options.OutputRoot, $"FlyScreen-{version}-flytools");
            PrepareCleanDir(dirOut);
            foreach (var f in Directory.EnumerateFiles(staging))
                File.Copy(f, Path.Combine(dirOut, Path.GetFileName(f)), overwrite: true);
            log($"插件工具目录已输出: {dirOut}");
        }

        // 同步到仓库 artifacts/flytools，供后续 App 发布复用 Bundled
        var sharedOut = Path.Combine(_repoRoot, "artifacts", "flytools");
        Directory.CreateDirectory(sharedOut);
        foreach (var f in Directory.EnumerateFiles(staging))
            File.Copy(f, Path.Combine(sharedOut, Path.GetFileName(f)), overwrite: true);
        log($"已同步到 {sharedOut}");
    }

    private async Task PublishRelayAsync(
        PublishTargetItem target,
        PublishOptions options,
        Action<string> log,
        IProgress<PublishProgress>? progress,
        CancellationToken ct)
    {
        var rid = target.ResolveDotnetRid()
                  ?? throw new InvalidOperationException("中继服务未选择有效运行时（请勿选 docker，或改用 Docker 分支）。");

        EnsureReleaseConfiguration(options, log);
        var version = ResolvePublishVersion(options, log);
        var staging = JobPath("relay");
        PrepareCleanDir(staging);

        Report(progress, 8, $"{target.DisplayName}：dotnet publish ({rid})…");
        var exit = await RunDotnetPublishAsync(target, options, staging, singleFile: true, log, ct)
            .ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"dotnet publish 失败 (relay/{rid})，退出码 {exit}");

        Report(progress, 72, $"{target.DisplayName}：清理调试符号…");
        StripDebugSymbols(staging, log);

        Directory.CreateDirectory(options.OutputRoot);
        Report(progress, 85, $"{target.DisplayName}：整理产物…");

        if (options.PackageMode == PackageMode.Installer || !LooksLikeSingleFilePublish(staging, rid))
        {
            var archiveName = $"FlyScreen-{version}-relay-{rid}.zip";
            var archivePath = Path.Combine(options.OutputRoot, archiveName);
            if (File.Exists(archivePath)) File.Delete(archivePath);
            ZipFile.CreateFromDirectory(staging, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            log($"中继服务安装包已输出: {archivePath}");
            CopyRelayComposeBeside(options.OutputRoot, log);
            return;
        }

        // 独立应用：优先单文件可执行，否则整目录 zip
        var primary = FindRelayPrimaryFile(staging, rid);
        if (primary != null)
        {
            var ext = Path.GetExtension(primary);
            if (string.IsNullOrEmpty(ext) && !rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
                ext = "";
            var destName = $"FlyScreen-{version}-relay-{rid}{ext}";
            var destPath = Path.Combine(options.OutputRoot, destName);
            File.Copy(primary, destPath, overwrite: true);
            log($"中继服务已输出: {destPath}");
        }
        else
        {
            var archiveName = $"FlyScreen-{version}-relay-{rid}.zip";
            var archivePath = Path.Combine(options.OutputRoot, archiveName);
            if (File.Exists(archivePath)) File.Delete(archivePath);
            ZipFile.CreateFromDirectory(staging, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            log($"中继服务（目录包）已输出: {archivePath}");
        }

        CopyRelayComposeBeside(options.OutputRoot, log);
    }

    private async Task PublishRelayDockerAsync(
        PublishTargetItem target,
        PublishOptions options,
        Action<string> log,
        IProgress<PublishProgress>? progress,
        CancellationToken ct)
    {
        EnsureReleaseConfiguration(options, log);
        var version = ResolvePublishVersion(options, log);
        var script = Path.Combine(_repoRoot, "src", "FlyScreen.Relay", "tools", "pack-docker.ps1");
        if (!File.Exists(script))
            throw new InvalidOperationException("未找到 src/FlyScreen.Relay/tools/pack-docker.ps1");

        Directory.CreateDirectory(options.OutputRoot);
        Report(progress, 10, $"{target.DisplayName}：Docker 构建镜像…");

        var host = (options.DockerSshHost ?? "").Trim();
        var isLocal = IsLocalDockerHost(host);
        var port = Math.Clamp(options.DockerSshPort, 1, 65535);
        var useTcp = !isLocal && (options.DockerUseTcp || string.IsNullOrWhiteSpace(options.DockerSshUser));
        if (isLocal)
            log("Docker 目标：本机（localhost）；国内默认 -Mirror。");
        else if (useTcp)
            log($"Docker 目标：TCP tcp://{host}:{port}（无认证）");
        else
            log($"Docker 目标：SSH.NET {options.DockerSshUser}@{host}:{port}");

        var relayRoot = Path.Combine(_repoRoot, "src", "FlyScreen.Relay");

        // SSH：远端 build/save 后拉回 tar，不要求本机 docker 客户端
        if (!isLocal && !useTcp)
        {
            try
            {
                await DockerRemotePacker.PackAsync(
                    relayRoot,
                    version,
                    options.OutputRoot,
                    host,
                    port,
                    options.DockerSshUser.Trim(),
                    string.IsNullOrWhiteSpace(options.DockerSshKeyPath) ? null : options.DockerSshKeyPath.Trim(),
                    string.IsNullOrEmpty(options.DockerSshPassword) ? null : options.DockerSshPassword,
                    mirror: true,
                    log,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Relay Docker SSH 远程打包失败：" + ex.Message, ex);
            }
        }
        else
        {
            // 本机 / TCP：仍走 pack-docker.ps1 + 本机 docker CLI
            EnsureLocalDockerCliAvailable(remote: !isLocal, log);
            var packArgs =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" " +
                $"-Mirror -Tag \"{version}\" -Save -OutDir \"{options.OutputRoot}\"";
            if (!isLocal && useTcp)
                packArgs += $" -SshHost \"{host}\" -SshPort {port} -Transport tcp";

            var env = MergeJobEnv(EnsureDockerPathEnv());
            var exit = await _runner.RunAsync("powershell", packArgs, _repoRoot, log, ct, env)
                .ConfigureAwait(false);
            if (exit != 0)
                throw new InvalidOperationException(
                    isLocal
                        ? "Relay Docker 打包失败（请确认本机 Docker Desktop 已启动，且 PATH 含 docker）"
                        : "Relay Docker TCP 打包失败（请确认远端已暴露 API，如 2375，且可访问；本机需 docker 客户端）");
        }

        Report(progress, 85, $"{target.DisplayName}：整理镜像产物…");
        var produced = Path.Combine(options.OutputRoot, $"flyscreen-relay-{version}.tar");
        if (!File.Exists(produced))
        {
            produced = Directory.EnumerateFiles(options.OutputRoot, "flyscreen-relay-*.tar")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("未找到 docker save 导出的 .tar 文件");
        }

        var destName = $"FlyScreen-{version}-relay-docker.tar";
        var destPath = Path.Combine(options.OutputRoot, destName);
        if (!string.Equals(produced, destPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(produced, destPath, overwrite: true);
            log($"已按发版命名复制: {destPath}");
        }
        else
        {
            log($"Docker 镜像已导出: {destPath}");
        }

        CopyRelayComposeBeside(options.OutputRoot, log);
        log("服务器导入: docker load -i " + destName);
        log($"然后: docker tag flyscreen-relay:{version} flyscreen-relay:latest && docker compose up -d");
    }

    private void CopyRelayComposeBeside(string outputRoot, Action<string> log)
    {
        var compose = Path.Combine(_repoRoot, "src", "FlyScreen.Relay", "docker-compose.yml");
        if (!File.Exists(compose)) return;
        var dest = Path.Combine(outputRoot, "docker-compose.relay.yml");
        File.Copy(compose, dest, overwrite: true);
        log($"已附带 compose: {dest}");
    }

    private static bool IsLocalDockerHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var h = host.Trim();
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || h.Equals("::1", StringComparison.OrdinalIgnoreCase)
               || h.Equals("(local)", StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> MergeJobEnv(IReadOnlyDictionary<string, string>? extra)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_jobEnv != null)
        {
            foreach (var (k, v) in _jobEnv)
                map[k] = v;
        }

        if (extra != null)
        {
            foreach (var (k, v) in extra)
                map[k] = v;
        }

        return map;
    }

    /// <summary>
    /// GUI 启动时常缺 Docker Desktop / CLI 注入的 PATH。
    /// 合并 Machine+User+Process，并探测常见安装目录中的 docker.exe。
    /// </summary>
    private static Dictionary<string, string> EnsureDockerPathEnv()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = GetMergedPathParts();
        var changed = false;

        foreach (var dir in EnumerateDockerCliDirectories())
        {
            if (parts.Any(p => p.Equals(dir, StringComparison.OrdinalIgnoreCase))) continue;
            parts.Insert(0, dir);
            changed = true;
        }

        // 从已合并 PATH 里若已有 docker.exe，确保其目录靠前
        var fromPath = FindDockerExeDirectory(parts);
        if (fromPath != null
            && !parts[0].Equals(fromPath, StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveAll(p => p.Equals(fromPath, StringComparison.OrdinalIgnoreCase));
            parts.Insert(0, fromPath);
            changed = true;
        }

        if (changed || parts.Count > 0)
            map["Path"] = string.Join(';', parts);
        return map;
    }

    /// <summary>远程/本机打包都需要本机 docker 客户端；缺则给出可读说明。</summary>
    private static void EnsureLocalDockerCliAvailable(bool remote, Action<string> log)
    {
        var env = EnsureDockerPathEnv();
        if (env.TryGetValue("Path", out var path))
            Environment.SetEnvironmentVariable("Path", path);

        if (FindDockerExeDirectory(GetMergedPathParts()) != null)
            return;

        log(remote
            ? "未找到本机 docker 客户端。SSH 远程打包仍须本机 docker.exe（只作客户端，不必启动本机引擎）。"
            : "未找到本机 docker 命令。");
        throw new InvalidOperationException(remote
            ? "远程 Docker 打包失败：本机无 docker 客户端。请安装 Docker Desktop，或仅安装 Docker CLI 后重新打开发布工具。"
            : "本机 Docker 打包失败：未找到 docker。请安装并启动 Docker Desktop，确认 PATH 含 Docker\\resources\\bin。");
    }

    private static List<string> GetMergedPathParts()
    {
        static IEnumerable<string> Split(string? raw)
            => (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var set = new List<string>();
        void AddRange(IEnumerable<string> items)
        {
            foreach (var p in items)
            {
                if (set.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
                set.Add(p);
            }
        }

        // Process 常缺 GUI 启动后才写入的 User PATH；显式合并三层
        AddRange(Split(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process)));
        AddRange(Split(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)));
        AddRange(Split(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine)));
        return set;
    }

    private static IEnumerable<string> EnumerateDockerCliDirectories()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var dir in new[]
                 {
                     Path.Combine(programFiles, "Docker", "Docker", "resources", "bin"),
                     Path.Combine(programFiles, "Docker", "Docker", "resources"),
                     Path.Combine(programFilesX86, "Docker", "Docker", "resources", "bin"),
                     Path.Combine(localApp, "Docker", "resources", "bin"),
                     Path.Combine(programFiles, "Docker", "cli-plugins"),
                     Path.Combine(userProfile, "scoop", "shims"),
                     Path.Combine(userProfile, "scoop", "apps", "docker", "current"),
                 })
        {
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "docker.exe")))
                yield return dir;
        }
    }

    private static string? FindDockerExeDirectory(IReadOnlyList<string> pathParts)
    {
        foreach (var dir in EnumerateDockerCliDirectories())
            return dir;

        foreach (var dir in pathParts)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "docker.exe")))
                    return dir;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static bool LooksLikeSingleFilePublish(string dir, string rid)
    {
        if (!Directory.Exists(dir)) return false;
        var files = Directory.GetFiles(dir);
        if (rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
            return files.Count(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) == 1
                   && files.Length <= 3;
        return files.Any(f =>
        {
            var name = Path.GetFileName(f);
            return !name.Contains('.') && new FileInfo(f).Length > 1_000_000;
        });
    }

    private static string? FindRelayPrimaryFile(string dir, string rid)
    {
        if (!Directory.Exists(dir)) return null;
        if (rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.EnumerateFiles(dir, "*.exe")
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault(f => Path.GetFileName(f).Contains("FlyScreen", StringComparison.OrdinalIgnoreCase)
                                     || Path.GetFileName(f).Contains("Relay", StringComparison.OrdinalIgnoreCase));
        }

        return Directory.EnumerateFiles(dir)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return !name.Contains('.') && new FileInfo(f).Length > 1_000_000;
            })
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
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
            var androidTfm = string.IsNullOrWhiteSpace(target.Framework)
                ? "net10.0-android"
                : target.Framework;
            // 与 Directory.Build.props 中 BaseIntermediateOutputPath 对齐
            var intermediateDir = Path.Combine(
                _repoRoot, "artifacts", "setup-work", _jobId, "obj", "FlyScreen.App.Maui",
                "Release", androidTfm, "android", "bin");
            if (Directory.Exists(intermediateDir))
            {
                Report(progress, 76, $"{target.DisplayName}：清理中间产物工具安装态…");
                StripToolsInstallStateFromDir(intermediateDir, log);
            }

            Report(progress, 78, $"{target.DisplayName}：签名 APK…");
            var apkScript = Path.Combine(_repoRoot, "build", "Ensure-MauiSignedApk.ps1");
            if (File.Exists(apkScript))
            {
                var args =
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{apkScript}\" " +
                    $"-OutDir \"{staging}\" -PackageId \"com.xuanniao.flyscreen\" " +
                    $"-IntermediateDir \"{intermediateDir}\"";
                exit = await _runner.RunAsync("powershell", args, _repoRoot, log, ct, _jobEnv)
                    .ConfigureAwait(false);
                if (exit != 0)
                    throw new InvalidOperationException("Ensure-MauiSignedApk 失败");
            }
            // Ensure-MauiSignedApk 内已在签名前剥离目录与 APK 内 Tools 安装态，勿在签名后再改 APK
        }

        Report(progress, 88, $"{target.DisplayName}：整理产物…");
        var finish = Path.Combine(_repoRoot, "build", "Finish-PublishToArtifacts.ps1");
        var kind = archiveInstaller ? "UnixArchive" : target.Kind;
        var version = ResolvePublishVersion(options, log);
        var finishArgs =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{finish}\" " +
            $"-SourceDir \"{staging}\" -Rid \"{target.Rid}\" -Kind {kind} " +
            $"-RepoRoot \"{_repoRoot}\" -OutDir \"{options.OutputRoot}\" " +
            $"-Version \"{version}\"";
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
        var version = ResolvePublishVersion(options, log);

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

        // 安装壳始终自包含单文件：依赖框架时若旁路留下空的 *.runtimeconfig.json，
        // 宿主会优先读它并报 “The document is empty”，Setup 无法启动。
        if (options.DeploymentMode != DeploymentMode.SelfContained)
            log("安装壳强制 Self-contained（避免空 runtimeconfig 导致 Setup 无法启动）。");
        var versionCode = VersionResolver.ComputeVersionCode(version);
        var publishArgs =
            $"publish \"{Path.Combine(installerDir, "FlyScreen.Installer.csproj")}\" " +
            $"-c Release -r win-x64 --self-contained true " +
            $"-p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true " +
            $"-p:DebugType=None -p:DebugSymbols=false " +
            $"-p:EnableCompressionInSingleFile=true " +
            $"-p:Version={version} -p:AssemblyVersion={version}.0 -p:FileVersion={version}.0 " +
            $"-p:InformationalVersion={version} -p:FlyScreenVersionCode={versionCode} " +
            $"-p:IgnoreGitVersion=true " +
            $"-p:FlyScreenPayloadZip=\"{payloadZip}\" " +
            $"-p:FlyScreenInstallerJson=\"{manifestPath}\" " +
            $"-p:BaseIntermediateOutputPath={QuoteMsBuildDir(installerObj)} " +
            $"-o \"{setupOut}\"";

        exit = await _runner.RunAsync("dotnet", publishArgs, _repoRoot, log, ct, _jobEnv)
            .ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException("安装程序 publish 失败");

        StripDebugSymbols(setupOut, log);
        ScrubSingleFileHostSidecars(setupOut, log);

        Report(progress, 92, $"{target.DisplayName}：输出 Setup…");
        var setupExe = Directory.EnumerateFiles(setupOut, "*.exe")
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("未找到安装程序 exe");

        Directory.CreateDirectory(options.OutputRoot);
        var finalName = $"FlyScreen-{version}-{target.Rid}-Setup.exe";
        var finalPath = Path.Combine(options.OutputRoot, finalName);
        File.Copy(setupExe, finalPath, overwrite: true);
        // 最终产物旁若残留空 runtimeconfig，同样会毒死改名后的 Setup
        ScrubSingleFileHostSidecars(options.OutputRoot, log, exeBaseName: Path.GetFileNameWithoutExtension(finalName));
        ScrubSingleFileHostSidecars(options.OutputRoot, log, exeBaseName: "FlyScreen-Setup");
        log($"安装包已输出: {finalPath}");
    }

    /// <summary>
    /// 单文件宿主旁的空/残留 runtimeconfig、deps 会使 .NET 宿主解析失败。
    /// </summary>
    private static void ScrubSingleFileHostSidecars(
        string dir,
        Action<string> log,
        string? exeBaseName = null)
    {
        if (!Directory.Exists(dir)) return;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(path);
            var scrub =
                name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".runtimeconfig.dev.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase);
            if (!scrub) continue;
            if (!string.IsNullOrWhiteSpace(exeBaseName)
                && !name.StartsWith(exeBaseName, StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("FlyScreen-Setup", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("FlyScreen.Setup", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                // 空文件或单文件发布旁路一律删除
                var len = new FileInfo(path).Length;
                if (len == 0 || scrub)
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch { /* ignore */ }
        }

        if (removed > 0)
            log($"已清理 {removed} 个可能干扰 Setup 启动的 runtimeconfig/deps 旁路文件。");
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
        else if (target.IsRelay)
        {
            var rid = target.ResolveDotnetRid();
            if (!string.IsNullOrWhiteSpace(rid))
                args.Add($"-r {rid}");
        }

        if (target.SupportsSelfContained)
            args.Add(selfContained ? "--self-contained true" : "--self-contained false");

        if (singleFile && target.Kind is not ("Apk" or "FlyTools" or "Docker"))
        {
            args.Add("-p:PublishSingleFile=true");
            args.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
            args.Add("-p:EnableCompressionInSingleFile=true");
            if (target.IsRelay)
                args.Add("-p:UseAppHost=true");
        }
        else if (!singleFile && target.Kind is not ("Apk" or "FlyTools" or "Docker"))
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

        // App 端可选集成 .flytool；默认跳过以减小体积
        if (target.SupportsFlyToolsBundle)
        {
            if (options.IncludeFlyTools)
            {
                var ids = NormalizeSelectedFlyToolIds(target, options, requireSelection: true);
                var bundledDir = JobPath("bundled-flytools");
                PrepareCleanDir(bundledDir);
                await PackFlyToolsToDirAsync(bundledDir, ids, log, ct).ConfigureAwait(false);
                args.Add("-p:FlyScreenSkipFlyTools=false");
                args.Add("-p:FlyScreenSkipFlyToolsPack=true");
                args.Add($"-p:FlyScreenFlyToolsOut=\"{bundledDir}\"");
                log($"集成插件工具 → Tools/Bundled：{string.Join(", ", ids)}");
            }
            else
            {
                args.Add("-p:FlyScreenSkipFlyTools=true");
                log("未集成插件工具（安装后可另行安装 .flytool）");
            }
        }

        AppendVersionMsBuildProps(args, options, log);

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

    private static readonly string[] ToolInstallStateFileNames =
    [
        "tools-install.json",
        "tools-shortcuts.json",
        "tools-local-programs.json",
        "tools-index.cache.json"
    ];

    /// <summary>
    /// 安装包/发布产物只携带 <c>Tools/Bundled/*.flytool</c>，不预装、不带安装记录。
    /// 只清理磁盘上的 Tools 目录；禁止用 Zip 改写 .apk（会破坏 APK Sig Block，
    /// 在 Android 13+ 表现为 PackageInfo 为 null / 无法安装）。
    /// Android 包内仅有 MauiAsset 的 Bundled，本就不会带 Installed。
    /// </summary>
    private static void StripToolsInstallStateFromDir(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) return;
        var removed = 0;

        foreach (var toolsRoot in Directory.EnumerateDirectories(dir, "Tools", SearchOption.AllDirectories))
        {
            try
            {
                removed += StripToolsRootOnDisk(toolsRoot);
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

    private static int StripToolsRootOnDisk(string toolsRoot)
    {
        var removed = 0;
        foreach (var name in ToolInstallStateFileNames)
        {
            var f = Path.Combine(toolsRoot, name);
            if (!File.Exists(f)) continue;
            File.Delete(f);
            removed++;
        }

        foreach (var sub in Directory.EnumerateDirectories(toolsRoot))
        {
            if (Path.GetFileName(sub).Equals("Bundled", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                Directory.Delete(sub, recursive: true);
                removed++;
            }
            catch { /* ignore */ }
        }

        return removed;
    }

    private async Task PackFlyToolsToDirAsync(
        string outDir,
        IReadOnlyList<string> toolIds,
        Action<string> log,
        CancellationToken ct)
    {
        var script = Path.Combine(_repoRoot, "build", "Pack-FlyTools.ps1");
        if (!File.Exists(script))
            throw new InvalidOperationException("未找到 build/Pack-FlyTools.ps1");

        Directory.CreateDirectory(outDir);
        var idsArg = string.Join(",", toolIds);
        var packArgs =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" " +
            $"-Configuration Release -OutRoot \"{outDir}\" -SkipAppCopy -ToolIds \"{idsArg}\"";
        log($"Pack-FlyTools -ToolIds {idsArg}");
        var exit = await _runner.RunAsync("powershell", packArgs, _repoRoot, log, ct, _jobEnv)
            .ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException("Pack-FlyTools 失败");
    }

    /// <summary>
    /// 规范化所选插件 Id：去重、校验存在性，并按目标端过滤不支持的插件。
    /// </summary>
    private static List<string> NormalizeSelectedFlyToolIds(
        PublishTargetItem target,
        PublishOptions options,
        bool requireSelection)
    {
        var platform = FlyToolCatalog.ResolvePlatform(target.Id);
        var selected = (options.SelectedFlyToolIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
        {
            if (requireSelection)
                throw new InvalidOperationException("请至少选择一个插件工具。");
            return [];
        }

        var known = FlyToolCatalog.All.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var ok = new List<string>();
        foreach (var id in selected)
        {
            if (!known.TryGetValue(id, out var tool))
                throw new InvalidOperationException($"未知插件工具：{id}");
            if (!FlyToolCatalog.SupportsPlatform(tool, platform))
                throw new InvalidOperationException(
                    $"插件「{tool.DisplayName}」不支持当前端（{platform ?? "不限"}），无法打包/集成。");
            ok.Add(tool.Id);
        }

        return ok;
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
        var version = ResolvePublishVersion(options, log);
        var versionCode = VersionResolver.ComputeVersionCode(version);
        var sc = options.DeploymentMode == DeploymentMode.SelfContained ? "true" : "false";
        var args =
            $"publish \"{uninstallerProj}\" -c Release -r win-x64 " +
            $"--self-contained {sc} -p:PublishSingleFile=true " +
            $"-p:DebugType=None -p:DebugSymbols=false " +
            $"-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true " +
            $"-p:Version={version} -p:AssemblyVersion={version}.0 -p:FileVersion={version}.0 " +
            $"-p:InformationalVersion={version} -p:FlyScreenVersionCode={versionCode} " +
            $"-p:IgnoreGitVersion=true " +
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

    private string ResolvePublishVersion(PublishOptions options, Action<string> log)
    {
        try
        {
            var version = VersionResolver.Normalize(
                string.IsNullOrWhiteSpace(options.Version)
                    ? VersionResolver.ReadCurrent(_repoRoot)
                    : options.Version);
            options.Version = version;
            return version;
        }
        catch (Exception ex)
        {
            log("版本号无效，回退到当前版本：" + ex.Message);
            var fallback = VersionResolver.ReadCurrent(_repoRoot);
            options.Version = fallback;
            return fallback;
        }
    }

    private static void AppendVersionMsBuildProps(List<string> args, PublishOptions options, Action<string> log)
    {
        var version = VersionResolver.Normalize(
            string.IsNullOrWhiteSpace(options.Version) ? "1.0.0" : options.Version);
        var versionCode = VersionResolver.ComputeVersionCode(version);
        // 禁用 Git 自动递增，确保程序集/清单/产物文件名使用 Setup UI 指定版本
        args.Add($"-p:Version={version}");
        args.Add($"-p:AssemblyVersion={version}.0");
        args.Add($"-p:FileVersion={version}.0");
        args.Add($"-p:InformationalVersion={version}");
        args.Add($"-p:FlyScreenVersionCode={versionCode}");
        args.Add($"-p:ApplicationDisplayVersion={version}");
        args.Add($"-p:ApplicationVersion={versionCode}");
        args.Add("-p:IgnoreGitVersion=true");
        log($"发布版本: {version} (versionCode={versionCode})");
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
        if (Directory.Exists(dir) && !TryDeleteDirectory(dir, retries: 6))
            throw new IOException($"无法清空目录（可能被占用）: {dir}");
        Directory.CreateDirectory(dir);
    }
}
