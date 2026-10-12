using System.IO;
using System.Net.Sockets;
using System.Threading.Channels;

namespace FlyScreen.Services.Network;

/// <summary>
/// 单个 TCP 连接上的双向会话。读写分离，发送队列避免阻塞输入线程。
/// </summary>
public sealed class PeerSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _sendQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sendLoop;
    private readonly Task _recvLoop;
    private DateTime _lastRecvUtc = DateTime.UtcNow;
    private DateTime _lastSendUtc = DateTime.UtcNow;
    private int _disposed;

    public string PeerName { get; private set; } = "";
    public string PeerIp { get; }
    public bool IsOutbound { get; }
    public bool Authenticated { get; private set; }
    public DateTime LastRecvUtc => _lastRecvUtc;
    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && _client.Connected;

    public event Action<PeerSession, NetMessage>? MessageReceived;
    public event Action<PeerSession, Exception?>? Disconnected;

    public PeerSession(TcpClient client, bool isOutbound)
    {
        _client = client;
        _client.NoDelay = true;
        _client.ReceiveBufferSize = 1024 * 1024;
        _client.SendBufferSize = 1024 * 1024;
        _stream = client.GetStream();
        IsOutbound = isOutbound;
        PeerIp = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "";
        _sendLoop = Task.Run(SendLoopAsync);
        _recvLoop = Task.Run(RecvLoopAsync);
    }

    public void SetPeerIdentity(string name)
    {
        PeerName = name;
        Authenticated = true;
    }

    public ValueTask SendAsync(MessageType type, byte[]? payload = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return ValueTask.CompletedTask;

        var frame = MessageCodec.Encode(type, payload ?? []);
        _sendQueue.Writer.TryWrite(frame);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendRawAsync(byte[] frame)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return ValueTask.CompletedTask;
        _sendQueue.Writer.TryWrite(frame);
        return ValueTask.CompletedTask;
    }

    public async Task SendAndFlushAsync(MessageType type, byte[]? payload = null, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var frame = MessageCodec.Encode(type, payload ?? []);
        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        _lastSendUtc = DateTime.UtcNow;
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (await _sendQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_sendQueue.Reader.TryRead(out var frame))
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        return;

                    await _stream.WriteAsync(frame.AsMemory()).ConfigureAwait(false);
                    _lastSendUtc = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex) when (IsBenignShutdown(ex))
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            RaiseDisconnected(ex);
        }
    }

    private async Task RecvLoopAsync()
    {
        var header = new byte[MessageCodec.HeaderSize];
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                // 不用 CancellationToken 取消 ReadAsync，避免关闭时抛 OperationCanceledException
                if (!await TryReadExactAsync(_stream, header).ConfigureAwait(false))
                    break;

                var type = (MessageType)header[0];
                var len = BitConverter.ToInt32(header, 1);
                if (len < 0 || len > MessageCodec.MaxPayload)
                    throw new InvalidDataException($"非法帧长度: {len}");

                var payload = len == 0 ? [] : new byte[len];
                if (len > 0 && !await TryReadExactAsync(_stream, payload).ConfigureAwait(false))
                    break;

                if (Volatile.Read(ref _disposed) != 0)
                    break;

                _lastRecvUtc = DateTime.UtcNow;
                MessageReceived?.Invoke(this, new NetMessage(type, payload));
            }
        }
        catch (Exception ex) when (IsBenignShutdown(ex))
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            RaiseDisconnected(ex);
            return;
        }
        finally
        {
            RaiseDisconnected(null);
        }
    }

    /// <summary>读满 buffer；对端关闭或本机释放时返回 false，不向外抛取消异常。</summary>
    private async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        try
        {
            while (offset < buffer.Length)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return false;

                var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset))
                    .ConfigureAwait(false);
                if (n == 0)
                    return false;
                offset += n;
            }

            return true;
        }
        catch (Exception ex) when (IsBenignShutdown(ex))
        {
            return false;
        }
    }

    private static bool IsBenignShutdown(Exception ex) =>
        ex is OperationCanceledException
            or ObjectDisposedException
            or EndOfStreamException
            or IOException
            or SocketException
            or ChannelClosedException;

    private void RaiseDisconnected(Exception? ex)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _sendQueue.Writer.TryComplete(); } catch { /* ignore */ }

        // 先关 socket，唤醒阻塞中的读写；勿依赖 CTS 取消 ReadAsync（会抛 OCE）
        try { _stream.Close(); } catch { /* ignore */ }
        try { _client.Close(); } catch { /* ignore */ }

        try
        {
            if (!_cts.IsCancellationRequested)
                _cts.Cancel();
        }
        catch { /* ignore */ }

        try { Disconnected?.Invoke(this, ex); } catch { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        RaiseDisconnected(null);
        try
        {
            await Task.WhenAll(_sendLoop, _recvLoop).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { /* ignore timeout / faults */ }

        try { _cts.Dispose(); } catch { /* ignore */ }
    }
}
