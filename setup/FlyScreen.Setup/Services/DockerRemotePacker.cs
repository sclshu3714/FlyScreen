using System.IO;
using System.IO.Compression;
using System.Text;

namespace FlyScreen.Setup.Services;

/// <summary>
/// SSH 远端打包 Relay 镜像：上传上下文 → docker build/save → 拉回 tar。
/// 不依赖本机 docker 客户端。
/// </summary>
internal static class DockerRemotePacker
{
    public static async Task PackAsync(
        string relayRoot,
        string version,
        string outputRoot,
        string host,
        int port,
        string user,
        string? keyPath,
        string? password,
        bool mirror,
        Action<string> log,
        CancellationToken ct)
    {
        if (!Directory.Exists(relayRoot))
            throw new DirectoryNotFoundException("未找到 Relay 工程目录: " + relayRoot);
        if (!File.Exists(Path.Combine(relayRoot, "Dockerfile")))
            throw new FileNotFoundException("未找到 Dockerfile", Path.Combine(relayRoot, "Dockerfile"));

        Directory.CreateDirectory(outputRoot);
        var imageTag = version.Trim();
        if (string.IsNullOrEmpty(imageTag))
            throw new ArgumentException("版本标签不能为空。", nameof(version));

        var client = await DockerSshHelper.ConnectAsync(host, port, user, keyPath, password, log, ct)
            .ConfigureAwait(false);
        DockerSshHelper.Session? session = null;
        var remoteWork = "/tmp/flyscreen-relay-pack-" + Guid.NewGuid().ToString("N");
        string? localZip = null;
        try
        {
            session = await DockerSshHelper.BeginSessionAsync(client, password, log, ct)
                .ConfigureAwait(false);
            var docker = session.DockerCommand;
            // mkdir/上传/解压始终不用 sudo；仅 docker build/save/prune 及 root 产物读写跟检测结果走
            log($"SSH 远端打包（无需本机 docker）。docker 调用：{docker}");

            await DockerSshHelper.RunAsync(
                client,
                "mkdir -p " + DockerSshHelper.ShQuote(remoteWork + "/src"),
                log,
                ct,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            localZip = Path.Combine(Path.GetTempPath(), $"flyscreen-relay-ctx-{Guid.NewGuid():N}.zip");
            log("正在打包构建上下文…");
            CreateContextZip(relayRoot, localZip);
            var zipSizeMb = Math.Round(new FileInfo(localZip).Length / (1024.0 * 1024.0), 2);
            log($"上传构建上下文 ({zipSizeMb} MB) → {remoteWork}/ctx.zip（同会话，不开 SFTP）");
            await DockerSshHelper.UploadFileAsync(
                client, localZip, remoteWork + "/ctx.zip", ct, TimeSpan.FromMinutes(15))
                .ConfigureAwait(false);

            log("解压构建上下文…");
            await DockerSshHelper.RunAsync(
                client,
                "cd " + DockerSshHelper.ShQuote(remoteWork) +
                " && (command -v python3 >/dev/null && python3 -c \"import zipfile; zipfile.ZipFile('ctx.zip').extractall('src')\"" +
                " || unzip -oq ctx.zip -d src)",
                log,
                ct,
                TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            var buildArgs = new StringBuilder();
            buildArgs.Append(docker).Append(" build");
            if (mirror)
            {
                buildArgs.Append(" --build-arg SDK_IMAGE=mcr.m.daocloud.io/dotnet/sdk:10.0");
                buildArgs.Append(" --build-arg RUNTIME_IMAGE=mcr.m.daocloud.io/dotnet/aspnet:10.0");
                log("使用国内镜像加速（DaoCloud mcr）");
            }

            buildArgs.Append(" -t ").Append(DockerSshHelper.ShQuote("flyscreen-relay:" + imageTag));
            buildArgs.Append(" -t ").Append(DockerSshHelper.ShQuote("flyscreen-relay:latest"));
            buildArgs.Append(" ").Append(DockerSshHelper.ShQuote(remoteWork + "/src"));

            log("远端 docker build …");
            await DockerSshHelper.RunAsync(
                client,
                buildArgs.ToString(),
                log,
                ct,
                TimeSpan.FromMinutes(60)).ConfigureAwait(false);

            var remoteTar = remoteWork + "/flyscreen-relay-" + imageTag + ".tar";
            log("远端 docker save …");
            await DockerSshHelper.RunAsync(
                client,
                docker + " save -o " + DockerSshHelper.ShQuote(remoteTar) +
                " " + DockerSshHelper.ShQuote("flyscreen-relay:" + imageTag),
                log,
                ct,
                TimeSpan.FromMinutes(30)).ConfigureAwait(false);

            // sudo docker save 产物属 root，先 chmod 再下载（下载仍可用 sudo cat 兜底）
            var sudo = DockerSshHelper.GetSudoPrefix(docker);
            if (!string.IsNullOrEmpty(sudo))
            {
                try
                {
                    await DockerSshHelper.RunAsync(
                        client,
                        sudo + " chmod 644 -- " + DockerSshHelper.ShQuote(remoteTar),
                        log,
                        ct,
                        TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log("chmod 镜像 tar 跳过: " + ex.Message);
                }
            }

            var localTar = Path.Combine(outputRoot, "flyscreen-relay-" + imageTag + ".tar");
            log("下载镜像 tar → " + localTar);
            await DockerSshHelper.DownloadFileAsync(
                client, remoteTar, localTar, ct, TimeSpan.FromMinutes(30), docker)
                .ConfigureAwait(false);

            var outMb = Math.Round(new FileInfo(localTar).Length / (1024.0 * 1024.0), 2);
            log($"已导出: {localTar} ({outMb} MB)");

            try
            {
                await DockerSshHelper.RunAsync(
                    client,
                    docker + " image prune -f",
                    log,
                    ct,
                    TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log("远端 image prune 跳过: " + ex.Message);
            }
        }
        finally
        {
            try
            {
                if (client.IsConnected)
                {
                    // 工作目录里可能有 root 拥有的 tar，优先 sudo 清理
                    var sudo = DockerSshHelper.GetSudoPrefix(session?.DockerCommand);
                    var rm = string.IsNullOrEmpty(sudo)
                        ? "rm -rf -- " + DockerSshHelper.ShQuote(remoteWork)
                        : sudo + " rm -rf -- " + DockerSshHelper.ShQuote(remoteWork);
                    using var cmd = client.CreateCommand(rm);
                    cmd.CommandTimeout = TimeSpan.FromMinutes(2);
                    cmd.Execute();
                }
            }
            catch
            {
                // ignore
            }

            try { session?.DisposeSessionArtifacts(); } catch { /* ignore */ }
            try
            {
                if (client.IsConnected) client.Disconnect();
            }
            catch
            {
                // ignore
            }

            client.Dispose();
            if (localZip != null)
            {
                try { File.Delete(localZip); } catch { /* ignore */ }
            }
        }
    }

    private static void CreateContextZip(string relayRoot, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        var rootFull = Path.GetFullPath(relayRoot);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(rootFull, file);
            if (ShouldExclude(rel)) continue;
            // zip 内统一用 /
            var entryName = rel.Replace('\\', '/');
            zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
    }

    private static bool ShouldExclude(string relativePath)
    {
        var n = relativePath.Replace('\\', '/');
        if (n.StartsWith("../", StringComparison.Ordinal)) return true;
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p.Equals("bin", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.Equals("obj", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.Equals("data", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.Equals("out", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.Equals("dist", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.Equals(".vs", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.Equals(".idea", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.Equals("tools", StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (n.EndsWith(".user", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.EndsWith(".suo", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Equals(".dockerignore", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
