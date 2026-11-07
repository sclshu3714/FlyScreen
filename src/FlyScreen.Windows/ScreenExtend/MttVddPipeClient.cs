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
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        Task? connectTask = null;
        try
        {
            // WhenAny + Dispose，不用 CancelAfter，避免调试器刷 OperationCanceledException
            connectTask = pipe.ConnectAsync();
            var delayTask = Task.Delay(TimeSpan.FromSeconds(2), ct);
            var completed = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);
            if (completed != connectTask)
                return false;
            await connectTask.ConfigureAwait(false);
            return pipe.IsConnected;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { await pipe.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
            Observe(connectTask);
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
        if (timeout <= TimeSpan.Zero)
            timeout = TimeSpan.FromSeconds(5);

        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        Task? connectTask = null;
        try
        {
            connectTask = pipe.ConnectAsync();
            var connectDelay = Task.Delay(timeout, ct);
            if (await Task.WhenAny(connectTask, connectDelay).ConfigureAwait(false) != connectTask)
            {
                ct.ThrowIfCancellationRequested();
                return "";
            }

            await connectTask.ConfigureAwait(false);

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
            if (!await TryWriteAsync(pipe, payload, timeout, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                return "";
            }

            try { await pipe.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (IOException) { return ""; }

            var ms = new MemoryStream();
            var buf = new byte[4096];
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    break;

                var n = await TryReadAsync(pipe, buf, TimeSpan.FromMilliseconds(remaining), ct)
                    .ConfigureAwait(false);
                if (n < 0)
                    break; // 超时或 I/O 失败：尽量返回已读内容
                if (n == 0)
                    break;
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
        finally
        {
            try { await pipe.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
            Observe(connectTask);
        }
    }

    private static async Task<bool> TryWriteAsync(
        PipeStream pipe, byte[] payload, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var writeTask = pipe.WriteAsync(payload).AsTask();
            var delayTask = Task.Delay(timeout, ct);
            if (await Task.WhenAny(writeTask, delayTask).ConfigureAwait(false) != writeTask)
            {
                Observe(writeTask);
                return false;
            }

            await writeTask.ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <returns>字节数；超时/失败返回 -1；对端关闭返回 0。</returns>
    private static async Task<int> TryReadAsync(
        PipeStream pipe, byte[] buf, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var readTask = pipe.ReadAsync(buf.AsMemory(0, buf.Length)).AsTask();
            var delayTask = Task.Delay(timeout, ct);
            if (await Task.WhenAny(readTask, delayTask).ConfigureAwait(false) != readTask)
            {
                Observe(readTask);
                return -1;
            }

            return await readTask.ConfigureAwait(false);
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static void Observe(Task? task)
    {
        if (task == null) return;
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
