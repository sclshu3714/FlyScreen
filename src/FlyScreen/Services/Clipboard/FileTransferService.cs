using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using FlyScreen.Helpers;
using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 文件传输：多文件队列、分块流式、无大小限制；接收完成后写入剪贴板供粘贴。
/// </summary>
public sealed class FileTransferService
{
    public const int ChunkSize = 512 * 1024;

    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly object _gate = new();
    private long _clipboardSequence;
    private long _lastAppliedRemoteSequence;
    private readonly Dictionary<Guid, ReceiveState> _receiving = new();
    private readonly Dictionary<long, BatchReceive> _batches = new();
    private readonly string _inboxDir;

    public ObservableCollection<TransferItem> Items { get; } = [];
    public event Action<string>? StatusMessage;

    public FileTransferService(AppSettings settings, ConnectionManager connections)
    {
        _settings = settings;
        _connections = connections;
        _inboxDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyScreen", "Inbox");
        Directory.CreateDirectory(_inboxDir);
        _connections.MessageReceived += OnMessage;
    }

    public long NextSequence() => Interlocked.Increment(ref _clipboardSequence);

    public Task OfferLocalFilesAsync(IReadOnlyList<string> paths, string? targetPeer = null)
    {
        if (!_settings.TransferFile || !_settings.ShareClipboard)
            return Task.CompletedTask;

        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) return Task.CompletedTask;

        var peers = string.IsNullOrEmpty(targetPeer)
            ? _connections.ConfiguredPeers().Where(_connections.IsPeerReachable).ToList()
            : [targetPeer!];

        if (peers.Count == 0)
        {
            NotifyStatus("没有已连接的对端，无法传输文件");
            return Task.CompletedTask;
        }

        var seq = NextSequence();
        foreach (var peer in peers)
        {
            for (var i = 0; i < files.Count; i++)
            {
                var path = files[i];
                var fi = new FileInfo(path);
                var item = new TransferItem
                {
                    Id = Guid.NewGuid(),
                    Direction = TransferDirection.Send,
                    FileName = fi.Name,
                    FullPath = path,
                    TotalBytes = fi.Length,
                    PeerName = peer,
                    State = TransferState.Queued
                };
                AddItem(item);
                _ = Task.Run(() => SendFileAsync(item, peer, seq, i, files.Count));
            }
        }

        return Task.CompletedTask;
    }

    private async Task SendFileAsync(TransferItem item, string peer, long seq, int index, int count)
    {
        try
        {
            item.State = TransferState.Transferring;
            var offer = MessageCodec.JsonBytes(new FileOfferPayload
            {
                TransferId = item.Id,
                FileName = item.FileName,
                TotalBytes = item.TotalBytes,
                Sequence = seq,
                FileIndex = index,
                FileCount = count
            });
            if (!await _connections.SendToAsync(peer, MessageType.FileOffer, offer).ConfigureAwait(false))
                throw new IOException($"对端未连接: {peer}");

            await using var fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[ChunkSize];
            long offset = 0;
            var lastUi = DateTime.UtcNow;
            while (true)
            {
                var n = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (n <= 0) break;

                var payload = new byte[24 + n];
                item.Id.ToByteArray().CopyTo(payload, 0);
                BitConverter.TryWriteBytes(payload.AsSpan(16, 8), offset);
                Buffer.BlockCopy(buffer, 0, payload, 24, n);
                if (!await _connections.SendToAsync(peer, MessageType.FileChunk, payload).ConfigureAwait(false))
                    throw new IOException("传输中断：连接已断开");
                offset += n;
                // 节流 UI 刷新，避免切换到传输页时刷爆绑定
                if ((DateTime.UtcNow - lastUi).TotalMilliseconds >= 80 || offset >= item.TotalBytes)
                {
                    item.BytesTransferred = offset;
                    lastUi = DateTime.UtcNow;
                }
            }

            var done = MessageCodec.JsonBytes(new FileControlPayload { TransferId = item.Id });
            await _connections.SendToAsync(peer, MessageType.FileComplete, done).ConfigureAwait(false);
            item.BytesTransferred = item.TotalBytes;
            item.State = TransferState.Completed;
            NotifyStatus($"已发送: {item.FileName} → {peer}");
        }
        catch (Exception ex)
        {
            item.State = TransferState.Failed;
            item.Status = ex.Message;
            try
            {
                var abort = MessageCodec.JsonBytes(new FileControlPayload
                {
                    TransferId = item.Id,
                    Reason = ex.Message
                });
                await _connections.SendToAsync(peer, MessageType.FileAbort, abort).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
    }

    private void NotifyStatus(string message) => UiThread.Post(() => StatusMessage?.Invoke(message));

    private void OnMessage(string peer, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.FileOffer:
                _ = HandleOfferAsync(peer, msg.Payload);
                break;
            case MessageType.FileChunk:
                HandleChunk(msg.Payload);
                break;
            case MessageType.FileComplete:
                _ = HandleCompleteAsync(msg.Payload);
                break;
            case MessageType.FileAbort:
                HandleAbort(msg.Payload);
                break;
            case MessageType.ClipboardText:
                HandleClipboardText(msg.Payload);
                break;
        }
    }

    private async Task HandleOfferAsync(string peer, byte[] payload)
    {
        var offer = MessageCodec.DecodeJson<FileOfferPayload>(payload);
        if (offer == null || !_settings.TransferFile) return;

        var dest = Path.Combine(_inboxDir, $"{offer.TransferId:N}_{Sanitize(offer.FileName)}");
        var item = new TransferItem
        {
            Id = offer.TransferId,
            Direction = TransferDirection.Receive,
            FileName = offer.FileName,
            FullPath = dest,
            TotalBytes = offer.TotalBytes,
            PeerName = peer,
            State = TransferState.Transferring
        };
        AddItem(item);

        var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read, ChunkSize, useAsync: true);
        lock (_gate)
        {
            _receiving[offer.TransferId] = new ReceiveState(item, fs, offer.Sequence);
            if (!_batches.TryGetValue(offer.Sequence, out var batch))
            {
                batch = new BatchReceive(offer.Sequence, Math.Max(1, offer.FileCount));
                _batches[offer.Sequence] = batch;
            }
            else if (offer.FileCount > batch.ExpectedCount)
            {
                batch.ExpectedCount = offer.FileCount;
            }
        }

        try
        {
            var accept = MessageCodec.JsonBytes(new FileControlPayload { TransferId = offer.TransferId });
            await _connections.SendToAsync(peer, MessageType.FileAccept, accept).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        NotifyStatus($"接收中: {offer.FileName} ← {peer}");
    }

    private void HandleChunk(byte[] payload)
    {
        if (payload.Length < 24) return;
        var id = new Guid(payload.AsSpan(0, 16));
        var offset = BitConverter.ToInt64(payload, 16);
        var dataLen = payload.Length - 24;

        ReceiveState? state;
        lock (_gate) _receiving.TryGetValue(id, out state);
        if (state == null) return;

        try
        {
            state.Stream.Position = offset;
            state.Stream.Write(payload, 24, dataLen);
            var transferred = Math.Max(state.Item.BytesTransferred, offset + dataLen);
            if ((DateTime.UtcNow - state.LastUiUtc).TotalMilliseconds >= 80
                || transferred >= state.Item.TotalBytes)
            {
                state.Item.BytesTransferred = transferred;
                state.LastUiUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            state.Item.State = TransferState.Failed;
            state.Item.Status = ex.Message;
        }
    }

    private async Task HandleCompleteAsync(byte[] payload)
    {
        var ctrl = MessageCodec.DecodeJson<FileControlPayload>(payload);
        if (ctrl == null) return;

        ReceiveState? state;
        lock (_gate) _receiving.Remove(ctrl.TransferId, out state);
        if (state == null) return;

        try
        {
            await state.Stream.FlushAsync().ConfigureAwait(false);
            await state.Stream.DisposeAsync().ConfigureAwait(false);
            state.Item.BytesTransferred = state.Item.TotalBytes;
            state.Item.State = TransferState.Completed;

            bool ready;
            List<string> files;
            lock (_gate)
            {
                if (!_batches.TryGetValue(state.Sequence, out var batch))
                {
                    batch = new BatchReceive(state.Sequence, 1);
                    _batches[state.Sequence] = batch;
                }

                batch.CompletedPaths.Add(state.Item.FullPath);
                ready = batch.CompletedPaths.Count >= batch.ExpectedCount
                        && !_receiving.Values.Any(r => r.Sequence == state.Sequence);
                files = ready ? batch.CompletedPaths.ToList() : [];
                if (ready) _batches.Remove(state.Sequence);
            }

            if (ready)
                await PublishFilesAsync(state.Sequence, files).ConfigureAwait(false);

            NotifyStatus($"已接收: {state.Item.FileName}");
        }
        catch (Exception ex)
        {
            state.Item.State = TransferState.Failed;
            state.Item.Status = ex.Message;
        }
    }

    private void HandleAbort(byte[] payload)
    {
        var ctrl = MessageCodec.DecodeJson<FileControlPayload>(payload);
        if (ctrl == null) return;
        ReceiveState? state;
        lock (_gate) _receiving.Remove(ctrl.TransferId, out state);
        if (state == null) return;
        try { state.Stream.Dispose(); } catch { /* ignore */ }
        try { if (File.Exists(state.Item.FullPath)) File.Delete(state.Item.FullPath); } catch { /* ignore */ }
        state.Item.State = TransferState.Failed;
        state.Item.Status = ctrl.Reason ?? "对端中止";
    }

    private void HandleClipboardText(byte[] payload)
    {
        var p = MessageCodec.DecodeJson<ClipboardTextPayload>(payload);
        if (p == null || !_settings.ShareClipboard) return;
        if (p.Sequence <= _lastAppliedRemoteSequence) return;
        _lastAppliedRemoteSequence = p.Sequence;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try { ClipboardBridge.SetTextSuppressing(p.Text); }
            catch { /* ignore */ }
        });
    }

    private async Task PublishFilesAsync(long sequence, List<string> files)
    {
        files = files.Where(File.Exists).ToList();
        if (files.Count == 0) return;
        if (sequence <= _lastAppliedRemoteSequence) return;
        _lastAppliedRemoteSequence = sequence;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                ClipboardBridge.SetFilesSuppressing(files);
                StatusMessage?.Invoke($"剪贴板已更新 {files.Count} 个文件，可直接粘贴");
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke("写入剪贴板失败: " + ex.Message);
            }
        });
    }

    public async Task BroadcastTextAsync(string text)
    {
        if (!_settings.ShareClipboard || string.IsNullOrEmpty(text)) return;
        var seq = NextSequence();
        var payload = MessageCodec.JsonBytes(new ClipboardTextPayload { Text = text, Sequence = seq });
        await _connections.BroadcastAsync(MessageType.ClipboardText, payload).ConfigureAwait(false);
    }

    public void ClearCompleted()
    {
        UiThread.Send(() =>
        {
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i].State is TransferState.Completed or TransferState.Cancelled or TransferState.Failed)
                    Items.RemoveAt(i);
            }
        });
    }

    private void AddItem(TransferItem item) => UiThread.Send(() => Items.Insert(0, item));

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.bin" : name;
    }

    private sealed class ReceiveState(TransferItem item, FileStream stream, long sequence)
    {
        public TransferItem Item { get; } = item;
        public FileStream Stream { get; } = stream;
        public long Sequence { get; } = sequence;
        public DateTime LastUiUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class BatchReceive(long sequence, int expectedCount)
    {
        public long Sequence { get; } = sequence;
        public int ExpectedCount { get; set; } = expectedCount;
        public List<string> CompletedPaths { get; } = [];
    }
}
