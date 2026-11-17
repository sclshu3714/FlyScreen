using System.IO;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FlyScreen.Setup.Services;

/// <summary>SSH.NET 连接与远端 docker 命令探测（含 sudo / askpass）。文件传输走同一会话，不开第二条 SFTP 连接。</summary>
internal static class DockerSshHelper
{
    public sealed record Session(
        SshClient Client,
        string DockerCommand,
        string? AskPassPath,
        string? Password)
    {
        public void DisposeSessionArtifacts()
        {
            if (string.IsNullOrEmpty(AskPassPath)) return;
            try
            {
                using var cmd = Client.CreateCommand("rm -f -- " + ShQuote(AskPassPath));
                cmd.Execute();
            }
            catch
            {
                // ignore
            }
        }
    }

    public static async Task<SshClient> ConnectAsync(
        string host,
        int port,
        string user,
        string? keyPath,
        string? password,
        Action<string>? log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("SSH 主机不能为空。", nameof(host));
        if (string.IsNullOrWhiteSpace(user))
            throw new ArgumentException("SSH 用户不能为空。", nameof(user));

        host = host.Trim();
        user = user.Trim();
        port = Math.Clamp(port, 1, 65535);

        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            if (!File.Exists(keyPath))
                throw new FileNotFoundException("SSH 私钥不存在: " + keyPath, keyPath);
            log?.Invoke($"SSH.NET：密钥认证 {user}@{host}:{port}");
        }
        else if (!string.IsNullOrEmpty(password))
        {
            log?.Invoke($"SSH.NET：密码认证 {user}@{host}:{port}");
        }
        else
        {
            throw new InvalidOperationException("SSH 模式须提供私钥路径或密码。");
        }

        await EnsureTcpReachableAsync(host, port, log, ct).ConfigureAwait(false);

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // 每次新建 AuthenticationMethod / ConnectionInfo，避免重试时复用失效状态
            AuthenticationMethod attemptAuth;
            if (!string.IsNullOrWhiteSpace(keyPath))
                attemptAuth = new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyPath!));
            else
                attemptAuth = new PasswordAuthenticationMethod(user, password!);

            var conn = new ConnectionInfo(host, port, user, attemptAuth)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            var client = new SshClient(conn);
            try
            {
                if (attempt > 1)
                    log?.Invoke($"SSH 重试连接 ({attempt}/3)…");
                await Task.Run(() => client.Connect(), ct).ConfigureAwait(false);
                // 远端 docker build 可能很长，保活避免中间设备掐断空闲会话
                client.KeepAliveInterval = TimeSpan.FromSeconds(30);
                log?.Invoke($"SSH 已连接 {user}@{host}:{port}");
                return client;
            }
            catch (Exception ex) when (attempt < 3 && IsTransientConnectFailure(ex))
            {
                last = ex;
                try { client.Dispose(); } catch { /* ignore */ }
                log?.Invoke($"SSH 连接暂失败：{DescribeSocket(ex)}，{attempt} 秒后重试…");
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { client.Dispose(); } catch { /* ignore */ }
                throw new InvalidOperationException(FormatConnectError(user, host, port, ex), ex);
            }
        }

        throw new InvalidOperationException(
            FormatConnectError(user, host, port, last ?? new IOException("连接失败")), last);
    }

    private static async Task EnsureTcpReachableAsync(
        string host, int port, Action<string>? log, CancellationToken ct)
    {
        // 先确认 IP:端口 TCP 可达，避免 SSH.NET 只抛底层 Socket 堆栈
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var tcp = new TcpClient();
                using var reg = ct.Register(() => { try { tcp.Close(); } catch { /* ignore */ } });
                var connectTask = tcp.ConnectAsync(host, port);
                var finished = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(8), ct))
                    .ConfigureAwait(false);
                if (finished != connectTask)
                    throw new SocketException((int)SocketError.TimedOut);
                await connectTask.ConfigureAwait(false);
                if (tcp.Connected)
                {
                    log?.Invoke($"TCP 可达 {host}:{port}");
                    return;
                }
            }
            catch (Exception ex) when (attempt < 3)
            {
                last = ex;
                log?.Invoke($"TCP 探测失败 ({attempt}/3)：{DescribeSocket(ex)}");
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        throw new InvalidOperationException(
            $"无法 TCP 连接到 {host}:{port}（SSH 端口）。" +
            $"请用 MobaXterm 同地址测试，或检查防火墙/是否关机。\n详情：{DescribeSocket(last)}",
            last);
    }

    private static bool IsTransientConnectFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException!)
        {
            if (e is SocketException se)
            {
                return se.SocketErrorCode is SocketError.TimedOut
                    or SocketError.ConnectionRefused
                    or SocketError.NetworkUnreachable
                    or SocketError.HostUnreachable
                    or SocketError.TryAgain
                    or SocketError.ConnectionReset;
            }

            if (e is SshOperationTimeoutException or SshConnectionException)
                return true;
            if (e.InnerException == null) break;
        }

        return false;
    }

    private static string DescribeSocket(Exception? ex)
    {
        if (ex == null) return "未知错误";
        var root = ex;
        while (root.InnerException != null)
            root = root.InnerException;
        if (root is SocketException se)
            return $"{se.SocketErrorCode}: {se.Message}";
        return root.Message;
    }

    /// <summary>
    /// 自动探测远端 docker 调用方式（按优先级，不加多余 sudo）：
    /// 1) 直接 <c>docker</c>（用户已在 docker 组 / root）
    /// 2) <c>sudo -n docker</c>（免密 sudo）
    /// 3) <c>sudo -A docker</c>（用 SSH 密码 askpass，仅密码登录时）
    /// 返回值供 build/save/prune 及「因 sudo 产生的 root 文件」读写复用。
    /// </summary>
    public static async Task<Session> BeginSessionAsync(
        SshClient client,
        string? password,
        Action<string>? log,
        CancellationToken ct)
    {
        log?.Invoke("正在检测远端 docker 权限（能直连则不加 sudo）…");

        if (await TryDockerInfoAsync(client, "docker", ct).ConfigureAwait(false))
        {
            log?.Invoke("检测结果：可直接使用 docker（无需 sudo）");
            return new Session(client, "docker", null, password);
        }

        log?.Invoke("直接 docker 不可用，尝试 sudo -n docker…");
        if (await TryDockerInfoAsync(client, "sudo -n docker", ct).ConfigureAwait(false))
        {
            log?.Invoke("检测结果：需免密 sudo（sudo -n docker）");
            return new Session(client, "sudo -n docker", null, password);
        }

        if (!string.IsNullOrEmpty(password))
        {
            log?.Invoke("免密 sudo 不可用，尝试用 SSH 密码经 sudo -A 提权…");
            var askPass = await InstallAskPassAsync(client, password, ct).ConfigureAwait(false);
            var wrapped = $"SUDO_ASKPASS={ShQuote(askPass)} sudo -A docker";
            if (await TryDockerInfoAsync(client, wrapped, ct).ConfigureAwait(false))
            {
                log?.Invoke("检测结果：需密码 sudo（sudo -A docker）");
                return new Session(client, wrapped, askPass, password);
            }

            try
            {
                using var rm = client.CreateCommand($"rm -f -- {ShQuote(askPass)}");
                rm.Execute();
            }
            catch
            {
                // ignore
            }
        }

        throw new InvalidOperationException(
            "远端无法使用 docker（permission denied 或未安装）。" +
            "可将该用户加入 docker 组并重新登录（推荐，之后自动不加 sudo），或配置免密/密码 sudo。");
    }

    public static async Task RunAsync(
        SshClient client,
        string command,
        Action<string> log,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(45);
        var text = await Task.Run(() => cmd.Execute(), ct).ConfigureAwait(false);
        WriteLines(log, text);
        WriteLines(log, cmd.Error);
        if (cmd.ExitStatus != 0)
        {
            throw new InvalidOperationException(
                $"远端命令失败 (exit {cmd.ExitStatus})：{TrimForError(command)}\n{TrimForError(cmd.Error)}");
        }
    }

    /// <summary>经已有 SSH 会话上传文件（cat &gt;），不另开 SFTP 连接。</summary>
    public static async Task UploadFileAsync(
        SshClient client,
        string localPath,
        string remotePath,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        await using var fs = File.OpenRead(localPath);
        await UploadStreamAsync(client, fs, remotePath, ct, timeout).ConfigureAwait(false);
    }

    public static async Task UploadBytesAsync(
        SshClient client,
        byte[] data,
        string remotePath,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        await using var ms = new MemoryStream(data, writable: false);
        await UploadStreamAsync(client, ms, remotePath, ct, timeout).ConfigureAwait(false);
    }

    public static async Task DownloadFileAsync(
        SshClient client,
        string remotePath,
        string localPath,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string? dockerCommandForSudo = null)
    {
        // sudo docker save 生成的文件属主是 root，普通 cat 会 Permission denied
        var sudo = GetSudoPrefix(dockerCommandForSudo);
        var cat = string.IsNullOrEmpty(sudo)
            ? "cat -- " + ShQuote(remotePath)
            : sudo + " cat -- " + ShQuote(remotePath);

        using var cmd = client.CreateCommand(cat);
        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(30);
        var exec = cmd.ExecuteAsync(ct);
        await using (var output = cmd.OutputStream)
        await using (var fs = File.Create(localPath))
        {
            await output.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        await exec.ConfigureAwait(false);
        if (cmd.ExitStatus != 0)
        {
            try { File.Delete(localPath); } catch { /* ignore */ }
            throw new InvalidOperationException(
                $"下载失败 (exit {cmd.ExitStatus})：{remotePath}\n{TrimForError(cmd.Error)}");
        }
    }

    /// <summary>
    /// 从 docker 命令提取 sudo 前缀：
    /// <c>sudo -n docker</c> → <c>sudo -n</c>；
    /// <c>SUDO_ASKPASS=… sudo -A docker</c> → <c>SUDO_ASKPASS=… sudo -A</c>。
    /// </summary>
    public static string? GetSudoPrefix(string? dockerCommand)
    {
        if (string.IsNullOrWhiteSpace(dockerCommand)) return null;
        if (!dockerCommand.Contains("sudo", StringComparison.Ordinal)) return null;
        var idx = dockerCommand.LastIndexOf(" docker", StringComparison.Ordinal);
        if (idx <= 0) return null;
        return dockerCommand[..idx].TrimEnd();
    }

    public static string ShQuote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "''";
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static async Task UploadStreamAsync(
        SshClient client,
        Stream content,
        string remotePath,
        CancellationToken ct,
        TimeSpan? timeout)
    {
        using var cmd = client.CreateCommand("cat > " + ShQuote(remotePath));
        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(30);
        var exec = cmd.ExecuteAsync(ct);
        await using (var input = cmd.CreateInputStream())
        {
            await content.CopyToAsync(input, ct).ConfigureAwait(false);
        }

        await exec.ConfigureAwait(false);
        if (cmd.ExitStatus != 0)
        {
            throw new InvalidOperationException(
                $"上传失败 (exit {cmd.ExitStatus})：{remotePath}\n{TrimForError(cmd.Error)}");
        }
    }

    private static async Task<bool> TryDockerInfoAsync(SshClient client, string dockerPrefix, CancellationToken ct)
    {
        try
        {
            using var cmd = client.CreateCommand(dockerPrefix + " info");
            cmd.CommandTimeout = TimeSpan.FromSeconds(25);
            await Task.Run(() => cmd.Execute(), ct).ConfigureAwait(false);
            return cmd.ExitStatus == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> InstallAskPassAsync(SshClient client, string password, CancellationToken ct)
    {
        var path = "/tmp/flyscreen-askpass-" + Guid.NewGuid().ToString("N");
        var body = Encoding.UTF8.GetBytes(
            "#!/bin/sh\nprintf '%s\\n' " + ShQuote(password) + "\n");
        await UploadBytesAsync(client, body, path, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await RunAsync(
            client,
            "chmod 700 -- " + ShQuote(path),
            _ => { },
            ct,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return path;
    }

    private static string FormatConnectError(string user, string host, int port, Exception ex)
    {
        var root = ex;
        while (root.InnerException != null)
            root = root.InnerException;

        var detail = root.Message;
        if (root is SocketException se)
            detail = $"Socket {se.SocketErrorCode}: {se.Message}";
        else if (ex is SshConnectionException or SshOperationTimeoutException)
            detail = ex.Message;

        return
            $"无法 SSH 连接到 {user}@{host}:{port}。\n" +
            $"请确认：1) 与 MobaXterm 相同 IP/端口；2) 本机可访问该地址；3) 端口是 SSH 不是 Docker API。\n" +
            $"详情：{detail}";
    }

    private static void WriteLines(Action<string> log, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) continue;
            log(line);
        }
    }

    private static string TrimForError(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        return s.Length <= 800 ? s : s[..800] + "…";
    }
}
