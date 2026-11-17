using System.IO;
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;

namespace FlyScreen.Setup.Services;

/// <summary>
/// 用 SSH.NET 在本机开 TCP 隧道，对接远端 <c>docker system dial-stdio</c>，
/// 使本机 docker CLI 走 <c>DOCKER_HOST=tcp://127.0.0.1:port</c>，无需外部 OpenSSH。
/// </summary>
internal sealed class DockerSshTunnel : IAsyncDisposable, IDisposable
{
    private readonly SshClient _client;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _bridges = new();
    private readonly object _gate = new();
    private Task? _acceptLoop;
    private bool _disposed;

    private DockerSshTunnel(SshClient client, TcpListener listener, int localPort)
    {
        _client = client;
        _listener = listener;
        LocalPort = localPort;
    }

    public int LocalPort { get; }

    public string DockerHost => $"tcp://127.0.0.1:{LocalPort}";

    public static async Task<DockerSshTunnel> StartAsync(
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

        AuthenticationMethod auth;
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            if (!File.Exists(keyPath))
                throw new FileNotFoundException("SSH 私钥不存在: " + keyPath, keyPath);
            auth = new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyPath));
            log?.Invoke($"SSH.NET：密钥认证 {user}@{host}:{port}");
        }
        else if (!string.IsNullOrEmpty(password))
        {
            auth = new PasswordAuthenticationMethod(user, password);
            log?.Invoke($"SSH.NET：密码认证 {user}@{host}:{port}");
        }
        else
        {
            throw new InvalidOperationException("SSH 模式须提供私钥路径或密码。");
        }

        var conn = new ConnectionInfo(host.Trim(), Math.Clamp(port, 1, 65535), user.Trim(), auth)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        var client = new SshClient(conn);
        try
        {
            await Task.Run(() => client.Connect(), ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var tunnel = new DockerSshTunnel(client, listener, localPort);
        tunnel._acceptLoop = tunnel.AcceptLoopAsync(tunnel._cts.Token);
        log?.Invoke($"SSH.NET 隧道已建立：DOCKER_HOST={tunnel.DockerHost} → {user}@{host} docker dial-stdio");
        return tunnel;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var bridge = BridgeClientAsync(tcp, ct);
                lock (_gate) _bridges.Add(bridge);
                _ = bridge.ContinueWith(t =>
                {
                    lock (_gate) _bridges.Remove(t);
                }, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                tcp?.Dispose();
                break;
            }
            catch
            {
                tcp?.Dispose();
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task BridgeClientAsync(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            using (tcp)
            {
                if (!_client.IsConnected)
                    return;

                // docker CLI ↔ SSH 远程「docker system dial-stdio」（等同官方 ssh helper）
                using var cmd = _client.CreateCommand("docker system dial-stdio");
                var executeTask = cmd.ExecuteAsync(ct);
                await using (var input = cmd.CreateInputStream())
                {
                    var remoteOut = cmd.OutputStream;
                    await using var local = tcp.GetStream();
                    var toRemote = local.CopyToAsync(input, ct);
                    var toLocal = remoteOut.CopyToAsync(local, ct);
                    try
                    {
                        await Task.WhenAny(toRemote, toLocal, executeTask).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // 正常关闭
                    }
                }

                try { await executeTask.ConfigureAwait(false); }
                catch { /* 连接结束后命令退出属预期 */ }
            }
        }
        catch
        {
            // 单连接失败不影响其它 docker API 调用
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener.Stop(); } catch { /* ignore */ }
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }

        Task[] pending;
        lock (_gate) pending = _bridges.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { /* ignore */ }

        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch { /* ignore */ }

        _client.Dispose();
        _cts.Dispose();
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
