using System.Collections.Concurrent;

namespace FlyScreen.Services.Network;

/// <summary>
/// 查询对端主屏分辨率/缩放；本机应答 DisplayInfoQuery。
/// </summary>
public sealed class DisplayInfoService : IDisposable
{
    private readonly ConnectionManager _connections;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DisplayInfoPayload>> _pending
        = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>本机主屏信息（宿主注入；未注入则不应答）。</summary>
    public Func<DisplayInfoPayload?>? GetLocalDisplayInfo { get; set; }

    public DisplayInfoService(ConnectionManager connections)
    {
        _connections = connections;
        _connections.MessageReceived += OnMessage;
    }

    public async Task<DisplayInfoPayload?> QueryPeerAsync(
        string peer,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(peer) || _disposed)
            return null;

        var tcs = new TaskCompletionSource<DisplayInfoPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[peer] = tcs;
        try
        {
            await _connections.SendToAsync(peer, MessageType.DisplayInfoQuery, Array.Empty<byte>())
                .ConfigureAwait(false);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            var delay = Task.Delay(timeout ?? TimeSpan.FromSeconds(2), ct);
            var done = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
            if (done == tcs.Task)
                return await tcs.Task.ConfigureAwait(false);
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(peer, out _);
        }
    }

    private void OnMessage(string peer, NetMessage msg)
    {
        if (_disposed) return;
        switch (msg.Type)
        {
            case MessageType.DisplayInfoQuery:
                _ = ReplyAsync(peer);
                break;
            case MessageType.DisplayInfoReply:
            {
                var info = MessageCodec.DecodeJson<DisplayInfoPayload>(msg.Payload);
                if (info == null || info.Width <= 0 || info.Height <= 0) return;
                if (_pending.TryGetValue(peer, out var tcs))
                    tcs.TrySetResult(info);
                break;
            }
        }
    }

    private async Task ReplyAsync(string peer)
    {
        try
        {
            var local = GetLocalDisplayInfo?.Invoke();
            if (local == null || local.Width <= 0 || local.Height <= 0) return;
            await _connections.SendToAsync(peer, MessageType.DisplayInfoReply, MessageCodec.JsonBytes(local))
                .ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connections.MessageReceived -= OnMessage;
        foreach (var kv in _pending)
            kv.Value.TrySetCanceled();
        _pending.Clear();
    }
}
