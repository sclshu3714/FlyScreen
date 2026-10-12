using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using FlyScreen.Helpers;
using FlyScreen.Models;
using FlyScreen.Native;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 剪贴板与文件传输：
/// 文件 / 内联内容（文本·HTML·RTF·CSV·图片）均为「复制仅宣告，进入粘贴机再拉取」；
/// 文件粘贴时分块流式、无大小限制；收件箱 guid 缓存，粘贴用原名暂存。
/// </summary>
public sealed class FileTransferService
{
    public const int ChunkSize = 512 * 1024;
    private static readonly Regex InboxNameRegex = new(
        @"^(?<id>[0-9a-fA-F]{32})_(?<name>.+)$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions HistoryJson = new() { WriteIndented = true };

    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly object _gate = new();
    private long _clipboardSequence;
    private long _lastAppliedRemoteSequence;
    private readonly Dictionary<Guid, ReceiveState> _receiving = new();
    private readonly Dictionary<long, BatchReceive> _batches = new();
    private readonly string _inboxDir;
    private readonly string _pasteDir;
    private readonly string _historyPath;
    private LocalPendingOffer? _localPending;
    private RemotePendingOffer? _remotePending;
    private TaskCompletionSource<IReadOnlyList<string>?>? _pullCompleteTcs;
    private int _pullInProgress;
    private int _ownsDelayedClipboard;
    private int _historySaveQueued;

    // 内联剪贴板（文本/HTML/RTF/CSV/图片）：宣告 + 进入本机再拉取
    private byte[]? _localPendingInlineBlob;
    private long _localPendingInlineSeq;
    private string _localPendingInlineSummary = "";
    private RemoteInlinePending? _remoteInlinePending;
    private TaskCompletionSource<byte[]?>? _inlinePullTcs;
    private int _inlinePullInProgress;
    private readonly Dictionary<long, InlineReceiveBuffer> _inlineReceiving = new();
    private TaskCompletionSource<RemoteFoldersPayload?>? _foldersQueryTcs;
    private string? _foldersQueryPeer;

    public ObservableCollection<TransferItem> Items { get; } = [];
    public event Action<string>? StatusMessage;
    /// <summary>收到对端文件宣告，需登记延迟剪贴板。</summary>
    public event Action? RemoteFilesAnnounced;
    /// <summary>收到对端内联剪贴板宣告（文本/富文本/截图等）。</summary>
    public event Action? RemoteInlineAnnounced;
    public string InboxDirectory => _inboxDir;

    public bool HasRemotePendingFiles
    {
        get { lock (_gate) return _remotePending != null; }
    }

    public bool HasRemotePendingInline
    {
        get { lock (_gate) return _remoteInlinePending != null; }
    }

    public bool OwnsDelayedClipboard => Volatile.Read(ref _ownsDelayedClipboard) != 0;

    /// <summary>对端宣告的文件名列表（用于剪贴板占位，启用“粘贴”菜单）。</summary>
    public IReadOnlyList<string> GetRemotePendingFileNames()
    {
        lock (_gate)
        {
            if (_remotePending == null) return [];
            return _remotePending.Files
                .Select(f => Sanitize(string.IsNullOrWhiteSpace(f.FileName) ? "file.bin" : f.FileName))
                .ToList();
        }
    }

    /// <summary>在粘贴目录创建原名空文件占位，供资源管理器启用粘贴。</summary>
    public IReadOnlyList<string> CreatePastePlaceholders(IReadOnlyList<string> fileNames)
    {
        try
        {
            if (Directory.Exists(_pasteDir))
            {
                try { Directory.Delete(_pasteDir, recursive: true); } catch { /* ignore */ }
            }
            Directory.CreateDirectory(_pasteDir);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (var raw in fileNames)
            {
                var name = MakeUniqueFileName(Sanitize(raw), used);
                var path = Path.Combine(_pasteDir, name);
                using (File.Create(path)) { /* 0 字节占位 */ }
                paths.Add(path);
            }

            return paths;
        }
        catch
        {
            return [];
        }
    }

    public bool IsInboxPath(string path) => IsUnderDirectory(path, _inboxDir) || IsUnderDirectory(path, _pasteDir);

    private static bool IsUnderDirectory(string path, string directory)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public FileTransferService(AppSettings settings, ConnectionManager connections)
    {
        _settings = settings;
        _connections = connections;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyScreen");
        _inboxDir = Path.Combine(root, "Inbox");
        // 粘贴暂存与收件箱分开，避免未粘贴时收件箱里出现占位/缓存
        _pasteDir = Path.Combine(root, "PasteStaging");
        _historyPath = Path.Combine(root, "transfer-history.json");
        Directory.CreateDirectory(_inboxDir);
        _connections.MessageReceived += OnMessage;
        LoadHistory();
    }

    public long NextSequence() => Interlocked.Increment(ref _clipboardSequence);

    /// <summary>
    /// 本机复制文件：仅向已连接对端宣告元数据，不立即传文件内容。
    /// </summary>
    public async Task AnnounceLocalFilesAsync(IReadOnlyList<string> paths)
    {
        if (!_settings.TransferFile || !_settings.ShareClipboard)
            return;

        var files = paths
            .Where(File.Exists)
            .Where(p => !IsInboxPath(p))
            .Select(p =>
            {
                var fi = new FileInfo(p);
                return new LocalPendingFile(fi.FullName, fi.Name, fi.Length);
            })
            .ToList();
        if (files.Count == 0) return;

        var seq = NextSequence();
        lock (_gate)
        {
            _localPending = new LocalPendingOffer(seq, files);
            _localPendingInlineBlob = null;
            _localPendingInlineSeq = 0;
            _localPendingInlineSummary = "";
        }

        var peers = _connections.ConfiguredPeers().Where(_connections.IsPeerReachable).ToList();
        if (peers.Count == 0)
        {
            NotifyStatus($"已复制 {files.Count} 个文件（等待对端粘贴时传输）");
            return;
        }

        var announce = MessageCodec.JsonBytes(new ClipboardFilesAnnouncePayload
        {
            Sequence = seq,
            SourceMachine = Environment.MachineName,
            Files = files.Select(f => new ClipboardFileAnnounceItem
            {
                FileName = f.FileName,
                TotalBytes = f.TotalBytes
            }).ToList()
        });

        foreach (var peer in peers)
            await _connections.SendToAsync(peer, MessageType.ClipboardFilesAnnounce, announce).ConfigureAwait(false);

        NotifyStatus($"已宣告 {files.Count} 个文件，对端粘贴时再传输");
    }

    /// <summary>
    /// 粘贴触发：向源端拉取文件，返回「原文件名」暂存路径供剪贴板粘贴。
    /// </summary>
    public async Task<IReadOnlyList<string>?> PullRemoteFilesForPasteAsync(CancellationToken ct = default)
    {
        if (!_settings.TransferFile || !_settings.ShareClipboard)
            return null;

        RemotePendingOffer? remote;
        lock (_gate) remote = _remotePending;
        if (remote == null) return null;

        if (!_connections.IsPeerReachable(remote.SourceMachine))
        {
            NotifyStatus($"无法拉取文件：{remote.SourceMachine} 未连接");
            return null;
        }

        if (Interlocked.CompareExchange(ref _pullInProgress, 1, 0) != 0)
        {
            TaskCompletionSource<IReadOnlyList<string>?>? existing;
            lock (_gate) existing = _pullCompleteTcs;
            if (existing != null)
                return await existing.Task.ConfigureAwait(false);
        }

        var tcs = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pullCompleteTcs = tcs;

        var payload = MessageCodec.JsonBytes(new ClipboardFilesPullPayload
        {
            Sequence = remote.Sequence,
            RequesterMachine = Environment.MachineName
        });

        NotifyStatus($"正在从 {remote.SourceMachine} 拉取 {remote.Files.Count} 个文件…");
        if (!await _connections.SendToAsync(remote.SourceMachine, MessageType.ClipboardFilesPull, payload)
                .ConfigureAwait(false))
        {
            CompletePullWaiter(null);
            NotifyStatus("拉取请求发送失败");
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));
            using (timeoutCts.Token.Register(() => tcs.TrySetResult(null)))
            {
                var files = await tcs.Task.ConfigureAwait(false);
                if (files == null || files.Count == 0)
                    NotifyStatus("文件拉取超时或失败");
                return files;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pullCompleteTcs, tcs))
                    _pullCompleteTcs = null;
            }
            Interlocked.Exchange(ref _pullInProgress, 0);
        }
    }

    public void MarkDelayedClipboardOwned(bool owned)
        => Volatile.Write(ref _ownsDelayedClipboard, owned ? 1 : 0);

    public void NotifyLocalClipboardOverride()
    {
        if (Volatile.Read(ref _pullInProgress) != 0) return;
        if (Volatile.Read(ref _inlinePullInProgress) != 0) return;
        lock (_gate)
        {
            _remotePending = null;
            _remoteInlinePending = null;
        }
        MarkDelayedClipboardOwned(false);
    }

    /// <summary>本机复制了文本/富文本/截图等：缓存打包数据并仅宣告元数据。</summary>
    public async Task AnnounceLocalInlineAsync(ClipboardInlineData data)
    {
        if (!_settings.ShareClipboard || data.IsEmpty) return;

        byte[] blob;
        try { blob = data.Pack(); }
        catch (Exception ex)
        {
            NotifyStatus("剪贴板打包失败: " + ex.Message);
            return;
        }

        if (blob.Length == 0) return;

        var summary = data.BuildSummary();
        var seq = NextSequence();
        lock (_gate)
        {
            _localPendingInlineBlob = blob;
            _localPendingInlineSeq = seq;
            _localPendingInlineSummary = summary;
            _localPending = null; // 内联优先于未粘贴的文件宣告
        }

        var peers = _connections.ConfiguredPeers().Where(_connections.IsPeerReachable).ToList();
        if (peers.Count == 0)
        {
            NotifyStatus($"已复制{summary}到剪贴板（等待对端粘贴时传输）");
            return;
        }

        var announce = MessageCodec.JsonBytes(new ClipboardInlineAnnouncePayload
        {
            Sequence = seq,
            SourceMachine = Environment.MachineName,
            TotalBytes = blob.Length,
            HasText = !string.IsNullOrEmpty(data.Text),
            HasHtml = !string.IsNullOrEmpty(data.Html),
            HasRtf = !string.IsNullOrEmpty(data.Rtf),
            HasCsv = !string.IsNullOrEmpty(data.Csv),
            HasImage = data.Png is { Length: > 0 },
            Summary = summary
        });

        foreach (var peer in peers)
            await _connections.SendToAsync(peer, MessageType.ClipboardInlineAnnounce, announce).ConfigureAwait(false);

        NotifyStatus($"已宣告{summary} {blob.Length / 1024.0:0.#} KB，对端粘贴时再传输");
    }

    /// <summary>粘贴目标机：向源端拉取内联剪贴板并解包。</summary>
    public async Task<ClipboardInlineData?> PullRemoteInlineForPasteAsync(CancellationToken ct = default)
    {
        if (!_settings.ShareClipboard) return null;

        RemoteInlinePending? remote;
        lock (_gate) remote = _remoteInlinePending;
        if (remote == null) return null;

        if (!_connections.IsPeerReachable(remote.SourceMachine))
        {
            NotifyStatus($"无法拉取剪贴板：{remote.SourceMachine} 未连接");
            return null;
        }

        if (Interlocked.CompareExchange(ref _inlinePullInProgress, 1, 0) != 0)
        {
            TaskCompletionSource<byte[]?>? existing;
            lock (_gate) existing = _inlinePullTcs;
            if (existing != null)
            {
                var existingBlob = await existing.Task.ConfigureAwait(false);
                return existingBlob is { Length: > 0 } ? SafeUnpackInline(existingBlob, remote.LegacyPngOnly) : null;
            }
        }

        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _inlinePullTcs = tcs;
            _inlineReceiving[remote.Sequence] = new InlineReceiveBuffer(remote.TotalBytes);
        }

        var pullType = remote.LegacyPngOnly
            ? MessageType.ClipboardImagePull
            : MessageType.ClipboardInlinePull;
        var payload = remote.LegacyPngOnly
            ? MessageCodec.JsonBytes(new ClipboardImagePullPayload
            {
                Sequence = remote.Sequence,
                RequesterMachine = Environment.MachineName
            })
            : MessageCodec.JsonBytes(new ClipboardInlinePullPayload
            {
                Sequence = remote.Sequence,
                RequesterMachine = Environment.MachineName
            });

        NotifyStatus($"正在从 {remote.SourceMachine} 拉取{remote.Summary}…");
        if (!await _connections.SendToAsync(remote.SourceMachine, pullType, payload).ConfigureAwait(false))
        {
            CompleteInlinePullWaiter(null);
            NotifyStatus("剪贴板拉取请求发送失败");
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
            using (timeoutCts.Token.Register(() => tcs.TrySetResult(null)))
            {
                var blob = await tcs.Task.ConfigureAwait(false);
                if (blob == null || blob.Length == 0)
                {
                    NotifyStatus("剪贴板拉取超时或失败");
                    return null;
                }

                var data = SafeUnpackInline(blob, remote.LegacyPngOnly);
                if (data == null || data.IsEmpty)
                {
                    NotifyStatus("剪贴板数据解析失败");
                    return null;
                }

                NotifyStatus($"{data.BuildSummary()}已就绪 {blob.Length / 1024.0:0.#} KB，可粘贴");
                return data;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inlinePullTcs, tcs))
                    _inlinePullTcs = null;
                _inlineReceiving.Remove(remote.Sequence);
            }
            Interlocked.Exchange(ref _inlinePullInProgress, 0);
        }
    }

    private static ClipboardInlineData? SafeUnpackInline(byte[] blob, bool legacyPngOnly)
    {
        try
        {
            return legacyPngOnly
                ? ClipboardInlineData.FromPngOnly(blob)
                : ClipboardInlineData.Unpack(blob);
        }
        catch
        {
            return null;
        }
    }

    private void CompleteInlinePullWaiter(byte[]? blob)
    {
        TaskCompletionSource<byte[]?>? waiter;
        lock (_gate)
        {
            waiter = _inlinePullTcs;
            _inlinePullTcs = null;
        }
        waiter?.TrySetResult(blob);
    }

    private void CompletePullWaiter(IReadOnlyList<string>? files)
    {
        TaskCompletionSource<IReadOnlyList<string>?>? waiter;
        lock (_gate)
        {
            waiter = _pullCompleteTcs;
            _pullCompleteTcs = null;
        }
        waiter?.TrySetResult(files);
    }

    private async Task SendPendingToPeerAsync(string peer, LocalPendingOffer pending)
    {
        for (var i = 0; i < pending.Files.Count; i++)
        {
            var file = pending.Files[i];
            if (!File.Exists(file.FullPath)) continue;

            var item = new TransferItem
            {
                Id = Guid.NewGuid(),
                Direction = TransferDirection.Send,
                FileName = file.FileName,
                FullPath = file.FullPath,
                TotalBytes = file.TotalBytes,
                PeerName = peer,
                State = TransferState.Queued
            };
            AddItem(item);
            await SendFileAsync(item, peer, pending.Sequence, i, pending.Files.Count,
                targetDirectory: null, relativePath: null, directToPath: false).ConfigureAwait(false);
        }
    }

    private async Task SendFileAsync(
        TransferItem item,
        string peer,
        long seq,
        int index,
        int count,
        string? targetDirectory,
        string? relativePath,
        bool directToPath)
    {
        try
        {
            item.State = TransferState.Transferring;
            var offer = MessageCodec.JsonBytes(new FileOfferPayload
            {
                TransferId = item.Id,
                FileName = Path.GetFileName(item.FullPath),
                TotalBytes = item.TotalBytes,
                Sequence = seq,
                FileIndex = index,
                FileCount = count,
                TargetDirectory = targetDirectory,
                RelativePath = relativePath ?? Path.GetFileName(item.FullPath),
                DirectToPath = directToPath
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
            QueueSaveHistory();
        }
        catch (Exception ex)
        {
            item.State = TransferState.Failed;
            item.Status = ex.Message;
            CompletePullWaiter(null);
            QueueSaveHistory();
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
            case MessageType.ClipboardFilesAnnounce:
                HandleAnnounce(peer, msg.Payload);
                break;
            case MessageType.ClipboardFilesPull:
                _ = HandlePullAsync(peer, msg.Payload);
                break;
            case MessageType.ClipboardInlineAnnounce:
                HandleInlineAnnounce(peer, msg.Payload, legacyPngOnly: false);
                break;
            case MessageType.ClipboardInlinePull:
                _ = HandleInlinePullAsync(peer, msg.Payload, legacyPngOnly: false);
                break;
            case MessageType.ClipboardInlineChunk:
                HandleInlineChunk(msg.Payload);
                break;
            case MessageType.ClipboardInlineComplete:
                HandleInlineComplete(msg.Payload, legacyPngOnly: false);
                break;
            case MessageType.ClipboardImageAnnounce:
                HandleLegacyImageAnnounce(peer, msg.Payload);
                break;
            case MessageType.ClipboardImagePull:
                _ = HandleInlinePullAsync(peer, msg.Payload, legacyPngOnly: true);
                break;
            case MessageType.ClipboardImageChunk:
                HandleInlineChunk(msg.Payload);
                break;
            case MessageType.ClipboardImageComplete:
                HandleInlineComplete(msg.Payload, legacyPngOnly: true);
                break;
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
            case MessageType.RemoteFoldersQuery:
                _ = HandleRemoteFoldersQueryAsync(peer);
                break;
            case MessageType.RemoteFoldersReply:
                HandleRemoteFoldersReply(peer, msg.Payload);
                break;
        }
    }

    /// <summary>查询对端常用保存位置。</summary>
    public async Task<RemoteFoldersPayload?> QueryRemoteFoldersAsync(string peer, CancellationToken ct = default)
    {
        if (!_connections.IsPeerReachable(peer)) return null;

        var tcs = new TaskCompletionSource<RemoteFoldersPayload?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _foldersQueryTcs = tcs;
            _foldersQueryPeer = peer;
        }

        if (!await _connections.SendToAsync(peer, MessageType.RemoteFoldersQuery, []).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (ReferenceEquals(_foldersQueryTcs, tcs))
                {
                    _foldersQueryTcs = null;
                    _foldersQueryPeer = null;
                }
            }
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            using (timeoutCts.Token.Register(() => tcs.TrySetResult(null)))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_foldersQueryTcs, tcs))
                {
                    _foldersQueryTcs = null;
                    _foldersQueryPeer = null;
                }
            }
        }
    }

    /// <summary>
    /// 直接推送文件/文件夹到对端指定目录（保留相对目录结构）。
    /// </summary>
    public async Task StartDirectTransferAsync(
        string peer,
        string targetDirectory,
        IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        if (!_settings.TransferFile)
        {
            NotifyStatus("未启用文件传输");
            return;
        }

        if (string.IsNullOrWhiteSpace(peer) || !_connections.IsPeerReachable(peer))
        {
            NotifyStatus("目标电脑未连接");
            return;
        }

        targetDirectory = targetDirectory.Trim().TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            NotifyStatus("请指定对端保存位置");
            return;
        }

        var entries = ExpandTransferEntries(paths);
        if (entries.Count == 0)
        {
            NotifyStatus("没有可传输的文件");
            return;
        }

        var seq = NextSequence();
        NotifyStatus($"直接传输 {entries.Count} 个文件 → {peer}:{targetDirectory}");

        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (!File.Exists(entry.FullPath)) continue;

            var item = new TransferItem
            {
                Id = Guid.NewGuid(),
                Direction = TransferDirection.Send,
                FileName = entry.RelativePath.Replace('\\', '/'),
                FullPath = entry.FullPath,
                TotalBytes = entry.TotalBytes,
                PeerName = peer,
                State = TransferState.Queued
            };
            AddItem(item);
            await SendFileAsync(
                item, peer, seq, i, entries.Count,
                targetDirectory, entry.RelativePath, directToPath: true).ConfigureAwait(false);
        }
    }

    private static List<DirectEntry> ExpandTransferEntries(IReadOnlyList<string> paths)
    {
        var result = new List<DirectEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.Trim();

            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (!seen.Add(fi.FullName)) continue;
                result.Add(new DirectEntry(fi.FullName, fi.Name, fi.Length));
            }
            else if (Directory.Exists(path))
            {
                var root = new DirectoryInfo(path);
                var rootPrefix = root.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;
                foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (!seen.Add(file.FullName)) continue;
                    var rel = file.FullName.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(root.Name, file.FullName[rootPrefix.Length..])
                        : Path.Combine(root.Name, file.Name);
                    result.Add(new DirectEntry(file.FullName, rel, file.Length));
                }
            }
        }

        return result;
    }

    private async Task HandleRemoteFoldersQueryAsync(string peer)
    {
        var payload = MessageCodec.JsonBytes(BuildLocalFoldersPayload());
        await _connections.SendToAsync(peer, MessageType.RemoteFoldersReply, payload).ConfigureAwait(false);
    }

    private void HandleRemoteFoldersReply(string peer, byte[] payload)
    {
        var folders = MessageCodec.DecodeJson<RemoteFoldersPayload>(payload);
        TaskCompletionSource<RemoteFoldersPayload?>? tcs;
        string? expected;
        lock (_gate)
        {
            tcs = _foldersQueryTcs;
            expected = _foldersQueryPeer;
        }

        if (tcs == null) return;
        if (expected != null && !string.Equals(expected, peer, StringComparison.OrdinalIgnoreCase))
            return;

        if (folders != null && string.IsNullOrWhiteSpace(folders.MachineName))
            folders.MachineName = peer;
        tcs.TrySetResult(folders);
    }

    public RemoteFoldersPayload BuildLocalFoldersPayload()
    {
        static string Special(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch { return ""; }
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        return new RemoteFoldersPayload
        {
            MachineName = Environment.MachineName,
            Desktop = Special(Environment.SpecialFolder.DesktopDirectory),
            Documents = Special(Environment.SpecialFolder.MyDocuments),
            Downloads = Directory.Exists(downloads) ? downloads : Special(Environment.SpecialFolder.UserProfile),
            Inbox = _inboxDir
        };
    }

    private void HandleAnnounce(string peer, byte[] payload)
    {
        var announce = MessageCodec.DecodeJson<ClipboardFilesAnnouncePayload>(payload);
        if (announce == null || !_settings.TransferFile || !_settings.ShareClipboard) return;
        if (announce.Files.Count == 0) return;

        var source = string.IsNullOrWhiteSpace(announce.SourceMachine) ? peer : announce.SourceMachine;
        lock (_gate)
        {
            _remotePending = new RemotePendingOffer(announce.Sequence, source, announce.Files.ToList());
            _remoteInlinePending = null; // 文件宣告覆盖未粘贴的内联内容
        }

        // 仅记录元数据，不改剪贴板、不拉文件；鼠标进入本机并粘贴时才传输
        NotifyStatus($"对端 {source} 已复制 {announce.Files.Count} 个文件（鼠标移到本机再粘贴才会传输）");
        try { RemoteFilesAnnounced?.Invoke(); } catch { /* ignore */ }
    }

    private void HandleLegacyImageAnnounce(string peer, byte[] payload)
    {
        var announce = MessageCodec.DecodeJson<ClipboardImageAnnouncePayload>(payload);
        if (announce == null || !_settings.ShareClipboard) return;
        if (announce.TotalBytes <= 0) return;

        var source = string.IsNullOrWhiteSpace(announce.SourceMachine) ? peer : announce.SourceMachine;
        RegisterRemoteInline(new RemoteInlinePending(
            announce.Sequence, source, announce.TotalBytes, "图片", legacyPngOnly: true));
        NotifyStatus($"对端 {source} 已复制图片 {announce.TotalBytes / 1024.0:0.#} KB（鼠标移到本机再粘贴才会传输）");
        try { RemoteInlineAnnounced?.Invoke(); } catch { /* ignore */ }
    }

    private void HandleInlineAnnounce(string peer, byte[] payload, bool legacyPngOnly)
    {
        var announce = MessageCodec.DecodeJson<ClipboardInlineAnnouncePayload>(payload);
        if (announce == null || !_settings.ShareClipboard) return;
        if (announce.TotalBytes <= 0) return;

        var source = string.IsNullOrWhiteSpace(announce.SourceMachine) ? peer : announce.SourceMachine;
        var summary = string.IsNullOrWhiteSpace(announce.Summary) ? "剪贴板" : announce.Summary;
        RegisterRemoteInline(new RemoteInlinePending(
            announce.Sequence, source, announce.TotalBytes, summary, legacyPngOnly));
        NotifyStatus($"对端 {source} 已复制{summary} {announce.TotalBytes / 1024.0:0.#} KB（鼠标移到本机再粘贴才会传输）");
        try { RemoteInlineAnnounced?.Invoke(); } catch { /* ignore */ }
    }

    private void RegisterRemoteInline(RemoteInlinePending pending)
    {
        lock (_gate)
        {
            _remoteInlinePending = pending;
            _remotePending = null; // 内联宣告覆盖未粘贴的文件
        }
    }

    private async Task HandleInlinePullAsync(string peer, byte[] payload, bool legacyPngOnly)
    {
        long seq;
        if (legacyPngOnly)
        {
            var pull = MessageCodec.DecodeJson<ClipboardImagePullPayload>(payload);
            if (pull == null || !_settings.ShareClipboard) return;
            seq = pull.Sequence;
        }
        else
        {
            var pull = MessageCodec.DecodeJson<ClipboardInlinePullPayload>(payload);
            if (pull == null || !_settings.ShareClipboard) return;
            seq = pull.Sequence;
        }

        byte[]? blob;
        long localSeq;
        string summary;
        lock (_gate)
        {
            blob = _localPendingInlineBlob;
            localSeq = _localPendingInlineSeq;
            summary = _localPendingInlineSummary;
        }

        if (blob == null || blob.Length == 0 || localSeq != seq)
        {
            NotifyStatus($"收到 {peer} 的剪贴板拉取，但本地没有对应内容");
            return;
        }

        // 旧版只懂纯 PNG：若当前是打包内联，尝试解出 PNG 再发
        byte[] sendBlob = blob;
        if (legacyPngOnly)
        {
            try
            {
                var unpacked = ClipboardInlineData.Unpack(blob);
                if (unpacked.Png is not { Length: > 0 })
                {
                    NotifyStatus($"对端 {peer} 为旧版，且当前剪贴板无图片可发");
                    return;
                }
                sendBlob = unpacked.Png;
                summary = "图片";
            }
            catch
            {
                // 可能本地本身就是旧 PNG 缓存
            }
        }

        var chunkType = legacyPngOnly ? MessageType.ClipboardImageChunk : MessageType.ClipboardInlineChunk;
        var completeType = legacyPngOnly ? MessageType.ClipboardImageComplete : MessageType.ClipboardInlineComplete;

        NotifyStatus($"向 {peer} 发送{summary} {sendBlob.Length / 1024.0:0.#} KB");

        try
        {
            for (long offset = 0; offset < sendBlob.Length; offset += ChunkSize)
            {
                var n = (int)Math.Min(ChunkSize, sendBlob.Length - offset);
                var frame = new byte[16 + n];
                BitConverter.TryWriteBytes(frame.AsSpan(0, 8), seq);
                BitConverter.TryWriteBytes(frame.AsSpan(8, 8), offset);
                Buffer.BlockCopy(sendBlob, (int)offset, frame, 16, n);
                if (!await _connections.SendToAsync(peer, chunkType, frame).ConfigureAwait(false))
                    throw new IOException("剪贴板传输中断");
            }

            var done = legacyPngOnly
                ? MessageCodec.JsonBytes(new ClipboardImageCompletePayload
                {
                    Sequence = seq,
                    TotalBytes = sendBlob.Length
                })
                : MessageCodec.JsonBytes(new ClipboardInlineCompletePayload
                {
                    Sequence = seq,
                    TotalBytes = sendBlob.Length
                });
            await _connections.SendToAsync(peer, completeType, done).ConfigureAwait(false);
            NotifyStatus($"{summary}已发送 → {peer}");
        }
        catch (Exception ex)
        {
            NotifyStatus("剪贴板发送失败: " + ex.Message);
        }
    }

    private void HandleInlineChunk(byte[] payload)
    {
        if (payload.Length < 16) return;
        var seq = BitConverter.ToInt64(payload, 0);
        var offset = BitConverter.ToInt64(payload, 8);
        var dataLen = payload.Length - 16;

        InlineReceiveBuffer? buf;
        lock (_gate) _inlineReceiving.TryGetValue(seq, out buf);
        if (buf == null) return;

        try
        {
            if (offset < 0 || offset + dataLen > buf.Data.Length) return;
            Buffer.BlockCopy(payload, 16, buf.Data, (int)offset, dataLen);
            buf.Received = Math.Max(buf.Received, offset + dataLen);
        }
        catch
        {
            CompleteInlinePullWaiter(null);
        }
    }

    private void HandleInlineComplete(byte[] payload, bool legacyPngOnly)
    {
        long sequence;
        long totalBytes;
        if (legacyPngOnly)
        {
            var done = MessageCodec.DecodeJson<ClipboardImageCompletePayload>(payload);
            if (done == null) return;
            sequence = done.Sequence;
            totalBytes = done.TotalBytes;
        }
        else
        {
            var done = MessageCodec.DecodeJson<ClipboardInlineCompletePayload>(payload);
            if (done == null) return;
            sequence = done.Sequence;
            totalBytes = done.TotalBytes;
        }

        InlineReceiveBuffer? buf;
        lock (_gate)
        {
            _inlineReceiving.Remove(sequence, out buf);
            if (_remoteInlinePending?.Sequence == sequence)
                _remoteInlinePending = null;
        }

        _ = totalBytes;
        if (buf == null || buf.Received < buf.Data.Length)
        {
            CompleteInlinePullWaiter(null);
            return;
        }

        if (sequence > _lastAppliedRemoteSequence)
            _lastAppliedRemoteSequence = sequence;

        CompleteInlinePullWaiter(buf.Data);
    }

    private async Task HandlePullAsync(string peer, byte[] payload)
    {
        var pull = MessageCodec.DecodeJson<ClipboardFilesPullPayload>(payload);
        if (pull == null || !_settings.TransferFile) return;

        LocalPendingOffer? pending;
        lock (_gate) pending = _localPending;
        if (pending == null || pending.Sequence != pull.Sequence)
        {
            NotifyStatus($"收到 {peer} 的拉取请求，但本地没有对应文件");
            return;
        }

        var target = string.IsNullOrWhiteSpace(pull.RequesterMachine) ? peer : pull.RequesterMachine;
        if (!string.Equals(target, peer, StringComparison.OrdinalIgnoreCase))
            target = peer;

        NotifyStatus($"向 {target} 发送 {pending.Files.Count} 个文件（粘贴请求）");
        await SendPendingToPeerAsync(target, pending).ConfigureAwait(false);
    }

    private async Task HandleOfferAsync(string peer, byte[] payload)
    {
        var offer = MessageCodec.DecodeJson<FileOfferPayload>(payload);
        if (offer == null || !_settings.TransferFile) return;

        string dest;
        var displayName = offer.FileName;
        try
        {
            if (offer.DirectToPath && !string.IsNullOrWhiteSpace(offer.TargetDirectory))
            {
                var rel = string.IsNullOrWhiteSpace(offer.RelativePath) ? offer.FileName : offer.RelativePath;
                dest = ResolveDirectDestination(offer.TargetDirectory, rel);
                displayName = rel.Replace('\\', '/');
                var parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
            }
            else
            {
                dest = Path.Combine(_inboxDir, $"{offer.TransferId:N}_{Sanitize(offer.FileName)}");
            }
        }
        catch (Exception ex)
        {
            NotifyStatus($"拒绝接收 {offer.FileName}: {ex.Message}");
            try
            {
                var abort = MessageCodec.JsonBytes(new FileControlPayload
                {
                    TransferId = offer.TransferId,
                    Reason = ex.Message
                });
                await _connections.SendToAsync(peer, MessageType.FileAbort, abort).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            return;
        }

        var item = new TransferItem
        {
            Id = offer.TransferId,
            Direction = TransferDirection.Receive,
            FileName = displayName,
            FullPath = dest,
            TotalBytes = offer.TotalBytes,
            PeerName = peer,
            State = TransferState.Transferring,
            BytesTransferred = 0
        };
        AddItem(item);

        var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read, ChunkSize, useAsync: true);
        lock (_gate)
        {
            _receiving[offer.TransferId] = new ReceiveState(item, fs, offer.Sequence, offer.DirectToPath);
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

        NotifyStatus(offer.DirectToPath
            ? $"直接接收: {displayName} → {dest}"
            : $"接收中: {offer.FileName} ← {peer}");
    }

    /// <summary>将对端目标目录与相对路径安全合成为本地绝对路径。</summary>
    private static string ResolveDirectDestination(string targetDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new InvalidOperationException("目标目录为空");

        var root = Path.GetFullPath(targetDirectory.Trim());
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (string.IsNullOrWhiteSpace(rel))
            throw new InvalidOperationException("相对路径为空");

        if (Path.IsPathRooted(rel))
            throw new InvalidOperationException("不允许绝对相对路径");

        var segments = rel.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".."))
            throw new InvalidOperationException("相对路径非法");

        var combined = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标路径越界");

        return combined;
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
            CompletePullWaiter(null);
            QueueSaveHistory();
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

            QueueSaveHistory();

            if (ready)
            {
                if (state.IsDirect)
                    NotifyStatus($"直接传输完成（{files.Count} 个文件）");
                else
                    FinishPulledBatch(state.Sequence, files);
            }

            NotifyStatus($"已接收: {state.Item.FileName}");
        }
        catch (Exception ex)
        {
            state.Item.State = TransferState.Failed;
            state.Item.Status = ex.Message;
            CompletePullWaiter(null);
            QueueSaveHistory();
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
        CompletePullWaiter(null);
        QueueSaveHistory();
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

    private void FinishPulledBatch(long sequence, List<string> inboxFiles)
    {
        inboxFiles = inboxFiles.Where(File.Exists).ToList();
        if (inboxFiles.Count == 0)
        {
            CompletePullWaiter(null);
            return;
        }

        if (sequence > _lastAppliedRemoteSequence)
            _lastAppliedRemoteSequence = sequence;

        lock (_gate)
        {
            if (_remotePending?.Sequence == sequence)
                _remotePending = null;
        }

        // 粘贴用原名路径，触发资源管理器覆盖/重命名对话框
        var pastePaths = PreparePastePaths(inboxFiles);
        NotifyStatus($"已就绪 {pastePaths.Count} 个文件（原名粘贴）");
        CompletePullWaiter(pastePaths.Count > 0 ? pastePaths : null);
    }

    /// <summary>
    /// 将收件箱 guid 缓存映射为原文件名硬链接/副本，供 CF_HDROP 粘贴。
    /// 若已有同名占位文件则就地覆盖，避免打断资源管理器已拿到的路径。
    /// </summary>
    private List<string> PreparePastePaths(IReadOnlyList<string> inboxPaths)
    {
        try
        {
            Directory.CreateDirectory(_pasteDir);

            var result = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var inboxPath in inboxPaths)
            {
                if (!File.Exists(inboxPath)) continue;

                var originalName = Sanitize(ResolveOriginalFileName(inboxPath));
                var uniqueName = MakeUniqueFileName(originalName, used);

                var pastePath = Path.Combine(_pasteDir, uniqueName);
                if (!LinkOrCopy(inboxPath, pastePath))
                    continue;
                result.Add(pastePath);
            }

            return result;
        }
        catch (Exception ex)
        {
            NotifyStatus("准备粘贴文件失败: " + ex.Message);
            return inboxPaths.Where(File.Exists).ToList();
        }
    }

    private string ResolveOriginalFileName(string inboxPath)
    {
        // 优先从 guid_原名 解析，避免回 UI 线程（粘贴等待时会死锁）
        var name = Path.GetFileName(inboxPath);
        var m = InboxNameRegex.Match(name);
        if (m.Success)
            return m.Groups["name"].Value;

        try
        {
            var snapshot = Items.ToArray();
            var item = snapshot.FirstOrDefault(i =>
                string.Equals(i.FullPath, inboxPath, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(item?.FileName))
                return item!.FileName;
        }
        catch { /* ignore collection race */ }

        return name;
    }

    private static string MakeUniqueFileName(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
        return $"{stem}_{Guid.NewGuid():N}{ext}";
    }

    private static bool LinkOrCopy(string source, string dest)
    {
        try
        {
            if (File.Exists(dest)) File.Delete(dest);
            if (NativeMethods.CreateHardLink(dest, source, IntPtr.Zero))
                return true;
            File.Copy(source, dest, overwrite: true);
            return File.Exists(dest);
        }
        catch
        {
            try
            {
                File.Copy(source, dest, overwrite: true);
                return File.Exists(dest);
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task BroadcastTextAsync(string text)
    {
        if (!_settings.ShareClipboard || string.IsNullOrEmpty(text)) return;
        var seq = NextSequence();
        var payload = MessageCodec.JsonBytes(new ClipboardTextPayload { Text = text, Sequence = seq });
        await _connections.BroadcastAsync(MessageType.ClipboardText, payload).ConfigureAwait(false);
    }

    /// <summary>
    /// 清除已完成/失败/取消记录。
    /// 仅删除「接收」项的收件箱缓存；发送历史只删列表记录，绝不删除本机源文件。
    /// </summary>
    public void ClearCompleted()
    {
        var removedReceiveCache = 0;
        UiThread.Send(() =>
        {
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                var item = Items[i];
                if (item.State is not (TransferState.Completed or TransferState.Cancelled or TransferState.Failed))
                    continue;
                if (TryDeleteReceiveCache(item))
                    removedReceiveCache++;
                Items.RemoveAt(i);
            }
        });
        CleanupPasteDir();
        SaveHistoryNow();
        NotifyStatus(removedReceiveCache > 0
            ? $"已清除记录，并删除 {removedReceiveCache} 个接收缓存"
            : "已清除记录（发送项未改动本地文件）");
    }

    /// <summary>
    /// 删除单条历史。接收项可删收件箱缓存；发送项只删记录，不删本机源文件。
    /// </summary>
    public void RemoveItem(TransferItem item, bool deleteCache = true)
    {
        var deletedCache = false;
        UiThread.Send(() =>
        {
            if (deleteCache)
                deletedCache = TryDeleteReceiveCache(item);
            Items.Remove(item);
        });
        SaveHistoryNow();
        NotifyStatus(deletedCache
            ? $"已删除记录及接收缓存: {item.FileName}"
            : $"已删除记录: {item.FileName}");
    }

    /// <summary>仅删除接收方向且位于 Inbox 的缓存；发送路径一律不动。</summary>
    private bool TryDeleteReceiveCache(TransferItem item)
    {
        // 传出历史：FullPath 是用户本机源文件，禁止删除
        if (item.Direction != TransferDirection.Receive)
            return false;
        if (string.IsNullOrWhiteSpace(item.FullPath))
            return false;

        string full;
        string inboxRoot;
        string pasteRoot;
        try
        {
            full = Path.GetFullPath(item.FullPath);
            inboxRoot = Path.GetFullPath(_inboxDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            pasteRoot = Path.GetFullPath(_pasteDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }
        catch
        {
            return false;
        }

        // 必须在收件箱内，且不是粘贴暂存（暂存由 CleanupPasteDir 统一清理）
        if (!full.StartsWith(inboxRoot, StringComparison.OrdinalIgnoreCase))
            return false;
        if (full.StartsWith(pasteRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            if (!File.Exists(full)) return false;
            File.Delete(full);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CleanupPasteDir()
    {
        try
        {
            if (Directory.Exists(_pasteDir))
                Directory.Delete(_pasteDir, recursive: true);
        }
        catch { /* ignore */ }
    }

    private void AddItem(TransferItem item)
    {
        // 必须 Post：若用 Send，在 UI 线程阻塞等待拉取时会死锁，拖死 Explorer
        UiThread.Post(() =>
        {
            var existing = Items.FirstOrDefault(x => x.Id == item.Id);
            if (existing != null)
            {
                existing.Direction = item.Direction;
                existing.FileName = item.FileName;
                existing.FullPath = item.FullPath;
                existing.TotalBytes = item.TotalBytes;
                existing.PeerName = item.PeerName;
                existing.State = item.State;
                existing.BytesTransferred = item.BytesTransferred;
                return;
            }
            Items.Insert(0, item);
        });
        QueueSaveHistory();
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath)) return;
            var json = File.ReadAllText(_historyPath);
            var entries = JsonSerializer.Deserialize<List<TransferHistoryDto>>(json, HistoryJson);
            if (entries == null || entries.Count == 0) return;

            foreach (var e in entries
                         .OrderByDescending(x => x.CreatedAt)
                         .Take(200))
            {
                var item = new TransferItem
                {
                    Id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id,
                    Direction = e.Direction,
                    FileName = e.FileName ?? "",
                    FullPath = e.FullPath ?? "",
                    TotalBytes = e.TotalBytes,
                    PeerName = e.PeerName ?? "",
                    CreatedAt = e.CreatedAt == default ? DateTime.Now : e.CreatedAt,
                    State = e.State,
                    BytesTransferred = e.State == TransferState.Completed ? e.TotalBytes : 0
                };

                if (item.Direction == TransferDirection.Receive
                    && item.State == TransferState.Completed
                    && !string.IsNullOrEmpty(item.FullPath)
                    && !File.Exists(item.FullPath))
                {
                    item.Status = "缓存已清除";
                }

                Items.Add(item);
            }
        }
        catch
        {
            // ignore corrupt history
        }
    }

    private void QueueSaveHistory()
    {
        if (Interlocked.Exchange(ref _historySaveQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400).ConfigureAwait(false);
                SaveHistoryNow();
            }
            finally
            {
                Interlocked.Exchange(ref _historySaveQueued, 0);
            }
        });
    }

    private void SaveHistoryNow()
    {
        try
        {
            List<TransferHistoryDto> snapshot = [];
            UiThread.Send(() =>
            {
                snapshot = Items
                    .Where(i => i.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled
                        or TransferState.Transferring or TransferState.Queued)
                    .Take(200)
                    .Select(i => new TransferHistoryDto
                    {
                        Id = i.Id,
                        Direction = i.Direction,
                        FileName = i.FileName,
                        FullPath = i.FullPath,
                        TotalBytes = i.TotalBytes,
                        PeerName = i.PeerName,
                        CreatedAt = i.CreatedAt,
                        State = i.State is TransferState.Transferring or TransferState.Queued
                            ? TransferState.Cancelled
                            : i.State
                    })
                    .ToList();
            });

            var dir = Path.GetDirectoryName(_historyPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(snapshot, HistoryJson));
        }
        catch { /* ignore */ }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.bin" : name;
    }

    private sealed class TransferHistoryDto
    {
        public Guid Id { get; set; }
        public TransferDirection Direction { get; set; }
        public string? FileName { get; set; }
        public string? FullPath { get; set; }
        public long TotalBytes { get; set; }
        public string? PeerName { get; set; }
        public DateTime CreatedAt { get; set; }
        public TransferState State { get; set; }
    }

    private sealed class LocalPendingFile(string fullPath, string fileName, long totalBytes)
    {
        public string FullPath { get; } = fullPath;
        public string FileName { get; } = fileName;
        public long TotalBytes { get; } = totalBytes;
    }

    private sealed class LocalPendingOffer(long sequence, List<LocalPendingFile> files)
    {
        public long Sequence { get; } = sequence;
        public List<LocalPendingFile> Files { get; } = files;
    }

    private sealed class RemotePendingOffer(long sequence, string sourceMachine, List<ClipboardFileAnnounceItem> files)
    {
        public long Sequence { get; } = sequence;
        public string SourceMachine { get; } = sourceMachine;
        public List<ClipboardFileAnnounceItem> Files { get; } = files;
    }

    private sealed class RemoteInlinePending(
        long sequence,
        string sourceMachine,
        long totalBytes,
        string summary,
        bool legacyPngOnly)
    {
        public long Sequence { get; } = sequence;
        public string SourceMachine { get; } = sourceMachine;
        public long TotalBytes { get; } = totalBytes;
        public string Summary { get; } = summary;
        public bool LegacyPngOnly { get; } = legacyPngOnly;
    }

    private sealed class InlineReceiveBuffer(long totalBytes)
    {
        public byte[] Data { get; } = new byte[totalBytes];
        public long Received { get; set; }
    }

    private sealed class ReceiveState(TransferItem item, FileStream stream, long sequence, bool isDirect = false)
    {
        public TransferItem Item { get; } = item;
        public FileStream Stream { get; } = stream;
        public long Sequence { get; } = sequence;
        public bool IsDirect { get; } = isDirect;
        public DateTime LastUiUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class DirectEntry(string fullPath, string relativePath, long totalBytes)
    {
        public string FullPath { get; } = fullPath;
        public string RelativePath { get; } = relativePath;
        public long TotalBytes { get; } = totalBytes;
    }

    private sealed class BatchReceive(long sequence, int expectedCount)
    {
        public long Sequence { get; } = sequence;
        public int ExpectedCount { get; set; } = expectedCount;
        public List<string> CompletedPaths { get; } = [];
    }
}
