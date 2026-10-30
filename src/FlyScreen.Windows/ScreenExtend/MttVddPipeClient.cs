using System.IO.Pipes;
using System.Text;

namespace FlyScreen.ScreenExtend;

/// <summary>
/// MTT Virtual Display Driver 命名管道客户端（命令 UTF-16LE，响应多为 UTF-8；一连接一命令）。
/// 管道：\\.\pipe\MTTVirtualDisplayPipe
/// </summary>
public static class MttVddPipeClient
{
    public const string PipeName = "MTTVirtualDisplayPipe";

    public static async Task<bool> IsPipePresentAsync(CancellationToken ct = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await SendCommandAsync("PING", TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return resp.Contains("PONG", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置虚拟显示器数量（会触发驱动重载，通常 2–8 秒）。</summary>
    public static Task<string> SetDisplayCountAsync(int count, CancellationToken ct = default)
        => SendCommandAsync($"SETDISPLAYCOUNT {Math.Clamp(count, 0, 8)}", TimeSpan.FromSeconds(60), ct);

    public static async Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
        try
        {
            // 驱动侧为 message 模式；设好后读到断开表示命令（含 RELOAD）已完成
            if (pipe.IsConnected)
                pipe.ReadMode = PipeTransmissionMode.Message;
        }
        catch (InvalidOperationException)
        {
            // 部分构建不支持改 ReadMode，仍按字节流读到断开
        }

        var payload = Encoding.Unicode.GetBytes(command);
        await pipe.WriteAsync(payload, cts.Token).ConfigureAwait(false);
        await pipe.FlushAsync(cts.Token).ConfigureAwait(false);

        var ms = new MemoryStream();
        var buf = new byte[4096];
        while (true)
        {
            int n;
            try
            {
                n = await pipe.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 超时：尽量返回已读内容
                break;
            }

            if (n <= 0) break;
            ms.Write(buf, 0, n);
        }

        var bytes = ms.ToArray();
        if (bytes.Length == 0) return "";
        // 响应多为 UTF-8 日志；偶发 UTF-16，优先 UTF-8
        var utf8 = Encoding.UTF8.GetString(bytes);
        if (utf8.Contains('\0'))
            return Encoding.Unicode.GetString(bytes);
        return utf8;
    }
}
