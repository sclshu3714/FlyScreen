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
        SingleWriter = false
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
    public bool IsAlive => !_cts.IsCancellationRequested && _client.Connected;

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
        var frame = MessageCodec.Encode(type, payload ?? []);
        return _sendQueue.Writer.WriteAsync(frame);
    }

    public ValueTask SendRawAsync(byte[] frame) => _sendQueue.Writer.WriteAsync(frame);

    public async Task SendAndFlushAsync(MessageType type, byte[]? payload = null, CancellationToken ct = default)
    {
        var frame = MessageCodec.Encode(type, payload ?? []);
        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        _lastSendUtc = DateTime.UtcNow;
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var frame in _sendQueue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _stream.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
                _lastSendUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) { }
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
            while (!_cts.IsCancellationRequested)
            {
                await ReadExactAsync(_stream, header, _cts.Token).ConfigureAwait(false);
                var type = (MessageType)header[0];
                var len = BitConverter.ToInt32(header, 1);
                if (len < 0 || len > MessageCodec.MaxPayload)
                    throw new InvalidDataException($"非法帧长度: {len}");

                var payload = len == 0 ? [] : new byte[len];
                if (len > 0)
                    await ReadExactAsync(_stream, payload, _cts.Token).ConfigureAwait(false);

                _lastRecvUtc = DateTime.UtcNow;
                MessageReceived?.Invoke(this, new NetMessage(type, payload));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RaiseDisconnected(ex);
        }
        finally
        {
            RaiseDisconnected(null);
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    private void RaiseDisconnected(Exception? ex)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { /* ignore */ }
        Disconnected?.Invoke(this, ex);
    }

    public async ValueTask DisposeAsync()
    {
        RaiseDisconnected(null);
        _sendQueue.Writer.TryComplete();
        try { await Task.WhenAny(Task.WhenAll(_sendLoop, _recvLoop), Task.Delay(500)).ConfigureAwait(false); } catch { /* ignore */ }
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _client.Dispose(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
