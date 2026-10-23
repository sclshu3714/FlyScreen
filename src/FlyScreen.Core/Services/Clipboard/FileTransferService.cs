using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FlyScreen.Helpers;
using FlyScreen.Models;
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
    private IClipboardApplySink _clipboard;
    private readonly object _gate = new();
    private long _clipboardSequence;
    private long _lastAppliedRemoteSequence;
    private readonly Dictionary<Guid, ReceiveState> _receiving = new();
    private readonly Dictionary<long, BatchReceive> _batches = new();
    /// <summary>文件夹批量在列表中的合并行：(sequence, groupKey) → UI 项。</summary>
    private readonly Dictionary<(long Seq, string Key), TransferItem> _folderUiItems = new();
    private readonly Dictionary<(long Seq, string Key), int> _folderUiCompleted = new();
    /// <summary>按列表项中止发送：TransferItem.Id → CTS。</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _itemAbort = new();
    /// <summary>当前正在发送的块：TransferItem.Id → (peer, transferId)。</summary>
    private readonly Dictionary<Guid, (string Peer, Guid TransferId)> _activeSends = new();
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
    /// <summary>粘贴占位顶层路径；拉取落盘时直接写入这些路径（含文件夹内容）。</summary>
    private string[]? _pasteMergeRoots;
    /// <summary>最近一次拉取并准备好的粘贴路径（供剪贴板挂载失败时重试）。</summary>
    private string[]? _lastPasteReadyPaths;
    /// <summary>最近一次粘贴批次的全部文件/目录（虚拟剪贴板 FileContents）。</summary>
    private ClipboardPasteEntry[]? _lastPasteEntries;

    /// <summary>文件块写盘队列：避免在 TCP 收包回调里同步写盘堵死键鼠消息。</summary>
    private readonly Channel<ChunkDiskJob> _chunkDiskChannel = Channel.CreateUnbounded<ChunkDiskJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

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
        => GetRemotePendingEntries().Select(e => e.Name).ToList();

    /// <summary>对端宣告项（含是否文件夹）。</summary>
    public IReadOnlyList<(string Name, bool IsDirectory)> GetRemotePendingEntries()
    {
        lock (_gate)
        {
            if (_remotePending == null) return [];
            return _remotePending.Files
                .Select(f => (
                    Sanitize(string.IsNullOrWhiteSpace(f.FileName) ? (f.IsDirectory ? "folder" : "file.bin") : f.FileName),
                    f.IsDirectory))
                .ToList();
        }
    }

    /// <summary>当前远程文件宣告序号；无 pending 时为 0。</summary>
    public long GetRemotePendingSequence()
    {
        lock (_gate) return _remotePending?.Sequence ?? 0;
    }

    /// <summary>最近一次已准备好的粘贴路径（内容已落盘，可挂 CF_HDROP）。</summary>
    public IReadOnlyList<string>? GetLastPasteReadyPaths()
    {
        lock (_gate) return _lastPasteReadyPaths;
    }

    /// <summary>最近一次粘贴批次明细（虚拟文件剪贴板）。</summary>
    public IReadOnlyList<ClipboardPasteEntry>? GetLastPasteEntries()
    {
        lock (_gate) return _lastPasteEntries;
    }

    /// <summary>按当前远程宣告创建文件/文件夹占位，供资源管理器启用粘贴。</summary>
    public IReadOnlyList<string> CreatePastePlaceholdersFromPending()
        => CreatePastePlaceholders(GetRemotePendingEntries());

    /// <summary>在粘贴目录创建原名空文件/空文件夹占位。</summary>
    public IReadOnlyList<string> CreatePastePlaceholders(IReadOnlyList<(string Name, bool IsDirectory)> entries)
    {
        try
        {
            Directory.CreateDirectory(_pasteDir);

            var batchDir = Path.Combine(_pasteDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(batchDir);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (var (raw, isDir) in entries)
            {
                var name = MakeUniqueFileName(Sanitize(raw), used);
                var path = Path.Combine(batchDir, name);
                if (isDir)
                    Directory.CreateDirectory(path);
                else
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

    /// <summary>兼容旧调用：全部按文件占位。</summary>
    public IReadOnlyList<string> CreatePastePlaceholders(IReadOnlyList<string> fileNames)
        => CreatePastePlaceholders(fileNames.Select(n => (n, false)).ToList());

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

    public FileTransferService(
        AppSettings settings,
        ConnectionManager connections,
        IClipboardApplySink? clipboard = null)
    {
        _settings = settings;
        _connections = connections;
        _clipboard = clipboard ?? NullClipboardApplySink.Instance;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyScreen");
        // Linux/Mac 无 Roaming 时回退到 LocalApplicationData / ~/.config 风格路径
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".flyscreen");
        _inboxDir = Path.Combine(root, "Inbox");
        // 粘贴暂存与收件箱分开，避免未粘贴时收件箱里出现占位/缓存
        _pasteDir = Path.Combine(root, "PasteStaging");
        _historyPath = Path.Combine(root, "transfer-history.json");
        Directory.CreateDirectory(_inboxDir);
        _connections.MessageReceived += OnMessage;
        LoadHistory();
        _ = Task.Run(ChunkDiskLoopAsync);
    }

    /// <summary>宿主可在 UI 就绪后注入平台剪贴板实现。</summary>
    public void SetClipboardSink(IClipboardApplySink sink)
        => _clipboard = sink ?? NullClipboardApplySink.Instance;

    public long NextSequence() => Interlocked.Increment(ref _clipboardSequence);

    /// <summary>
    /// 本机复制文件：仅向已连接对端宣告元数据，不立即传文件内容。
    /// 返回 true 表示已接受并尝试宣告（调用方据此更新去重 key）。
    /// </summary>
    public async Task<bool> AnnounceLocalFilesAsync(IReadOnlyList<string> paths)
    {
        if (!_settings.TransferFile || !_settings.ShareClipboard)
        {
            NotifyStatus("未启用共享剪贴板或文件传输，无法宣告");
            return false;
        }

        if (paths.Count == 0) return false;

        var items = new List<LocalPendingFile>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.Trim();
            if (IsInboxPath(path)) continue;

            try
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    items.Add(new LocalPendingFile(fi.FullName, fi.Name, fi.Length, isDirectory: false));
                }
                else if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    long bytes = 0;
                    try
                    {
                        foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
                            bytes += f.Length;
                    }
                    catch { /* 部分子项不可读仍宣告文件夹 */ }

                    items.Add(new LocalPendingFile(di.FullName, di.Name, bytes, isDirectory: true));
                }
            }
            catch
            {
                // skip
            }
        }

        if (items.Count == 0)
        {
            NotifyStatus($"剪贴板有 {paths.Count} 个路径但无法读取（可能已移动或无权访问）");
            return false;
        }

        var seq = NextSequence();
        lock (_gate)
        {
            _localPending = new LocalPendingOffer(seq, items);
            _localPendingInlineBlob = null;
            _localPendingInlineSeq = 0;
            _localPendingInlineSummary = "";
        }

        var peers = _connections.ConfiguredPeers().ToList();
        var announce = MessageCodec.JsonBytes(new ClipboardFilesAnnouncePayload
        {
            Sequence = seq,
            SourceMachine = LocalPeerIdentity.MachineName,
            Files = items.Select(f => new ClipboardFileAnnounceItem
            {
                FileName = f.FileName,
                TotalBytes = f.TotalBytes,
                IsDirectory = f.IsDirectory
            }).ToList()
        });

        var sent = 0;
        foreach (var peer in peers)
        {
            if (await _connections.SendToAsync(peer, MessageType.ClipboardFilesAnnounce, announce)
                    .ConfigureAwait(false))
                sent++;
        }

        var fileCount = items.Count(i => !i.IsDirectory);
        var dirCount = items.Count(i => i.IsDirectory);
        var summary = dirCount == 0
            ? $"{fileCount} 个文件"
            : fileCount == 0
                ? $"{dirCount} 个文件夹"
                : $"{fileCount} 个文件+{dirCount} 个文件夹";

        if (peers.Count == 0)
            NotifyStatus($"已复制 {summary}（布局中无对端）");
        else if (sent == 0)
            NotifyStatus($"已复制 {summary}（对端未连接，连上后需重新复制）");
        else
            NotifyStatus($"已复制 {summary} → {sent} 台，对端粘贴时传输");

        return true;
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
        if (remote == null)
        {
            // 已拉完但剪贴板尚未挂上：返回上次准备好的路径
            lock (_gate)
            {
                if (_lastPasteReadyPaths is { Length: > 0 })
                    return _lastPasteReadyPaths;
            }

            return null;
        }

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

        NotifyStatus($"正在从 {remote.SourceMachine} 接收 {remote.Files.Count} 个文件…");
        try
        {
            if (!await _connections.SendToAsync(remote.SourceMachine, MessageType.ClipboardFilesPull, payload)
                    .ConfigureAwait(false))
            {
                CompletePullWaiter(null);
                NotifyStatus("拉取请求发送失败");
                return null;
            }

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

    /// <summary>
    /// 登记粘贴占位根路径。随后 <see cref="PullRemoteFilesForPasteAsync"/> 会把文件/文件夹内容
    /// 直接写入这些路径（避免先出空文件夹再合并失败）。
    /// </summary>
    public void SetPasteMergeRoots(IReadOnlyList<string>? roots)
    {
        lock (_gate)
            _pasteMergeRoots = roots is { Count: > 0 } ? roots.ToArray() : null;
    }

    public void NotifyLocalClipboardOverride()
    {
        if (Volatile.Read(ref _pullInProgress) != 0) return;
        if (Volatile.Read(ref _inlinePullInProgress) != 0) return;
        lock (_gate)
        {
            _remotePending = null;
            _remoteInlinePending = null;
            _lastPasteReadyPaths = null;
            _lastPasteEntries = null;
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
        var expanded = ExpandTransferEntries(pending.Files.Select(f => f.FullPath).ToList());
        if (expanded.Count == 0)
        {
            NotifyStatus($"向 {peer} 发送失败：没有可传的文件");
            return;
        }

        NotifyStatus($"向 {peer} 发送 {expanded.Count} 个文件（含文件夹展开）");
        await SendExpandedGroupsAsync(
            peer, pending.Sequence, expanded,
            targetDirectory: null, directToPath: false).ConfigureAwait(false);
    }

    /// <summary>按顶层文件/文件夹分组发送；文件夹在列表中只占一行。</summary>
    private async Task SendExpandedGroupsAsync(
        string peer,
        long seq,
        List<DirectEntry> expanded,
        string? targetDirectory,
        bool directToPath,
        CancellationToken ct = default)
    {
        var groups = GroupSendEntries(expanded);
        var globalIndex = 0;
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (g.IsFolder)
            {
                var ui = new TransferItem
                {
                    Id = Guid.NewGuid(),
                    Direction = TransferDirection.Send,
                    FileName = g.Key,
                    FullPath = g.RootPath,
                    PeerName = peer,
                    Sequence = seq,
                    GroupKey = g.Key,
                    State = TransferState.Queued,
                    BatchCount = g.Files.Count,
                    BatchIndex = 0,
                    BatchTotalBytes = g.Files.Sum(f => f.TotalBytes),
                    TotalBytes = g.Files.Count > 0 ? g.Files[0].TotalBytes : 0
                };
                AddItem(ui);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, GetItemAbortToken(ui));
                var itemCt = linked.Token;

                for (var i = 0; i < g.Files.Count; i++)
                {
                    itemCt.ThrowIfCancellationRequested();
                    var entry = g.Files[i];
                    if (!File.Exists(entry.FullPath)) continue;

                    ui.BatchIndex = i + 1;
                    ui.TotalBytes = entry.TotalBytes;
                    ui.BytesTransferred = 0;
                    ui.State = TransferState.Transferring;

                    var transferId = Guid.NewGuid();
                    var ok = await SendFileCoreAsync(
                        ui, transferId, entry.FullPath, entry.TotalBytes,
                        peer, seq, globalIndex++, expanded.Count,
                        targetDirectory, entry.RelativePath, directToPath,
                        groupKey: g.Key, groupIndex: i, groupCount: g.Files.Count,
                        itemCt).ConfigureAwait(false);
                    if (!ok)
                    {
                        ClearItemAbort(ui.Id);
                        return;
                    }
                }

                if (itemCt.IsCancellationRequested)
                {
                    ClearItemAbort(ui.Id);
                    return;
                }

                ui.BytesTransferred = ui.TotalBytes = ui.BatchTotalBytes > 0 ? ui.BatchTotalBytes : ui.TotalBytes;
                ui.State = TransferState.Completed;
                ClearItemAbort(ui.Id);
                NotifyStatus($"已发送文件夹: {g.Key} → {peer}");
                QueueSaveHistory();
            }
            else
            {
                var entry = g.Files[0];
                if (!File.Exists(entry.FullPath)) continue;
                var item = new TransferItem
                {
                    Id = Guid.NewGuid(),
                    Direction = TransferDirection.Send,
                    FileName = entry.RelativePath.Replace('\\', '/'),
                    FullPath = entry.FullPath,
                    TotalBytes = entry.TotalBytes,
                    PeerName = peer,
                    Sequence = seq,
                    GroupKey = null,
                    State = TransferState.Queued
                };
                AddItem(item);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, GetItemAbortToken(item));
                await SendFileCoreAsync(
                    item, item.Id, entry.FullPath, entry.TotalBytes,
                    peer, seq, globalIndex++, expanded.Count,
                    targetDirectory, entry.RelativePath, directToPath,
                    groupKey: null, groupIndex: 0, groupCount: 1,
                    linked.Token).ConfigureAwait(false);
                ClearItemAbort(item.Id);
            }
        }
    }

    private static List<SendGroup> GroupSendEntries(List<DirectEntry> expanded)
    {
        var result = new List<SendGroup>();
        var folderMap = new Dictionary<string, SendGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in expanded)
        {
            var rel = e.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var key = parts[0];
                if (!folderMap.TryGetValue(key, out var g))
                {
                    var rootPath = e.FullPath;
                    try
                    {
                        // .../Folder/sub/file → Folder 目录
                        var idx = e.FullPath.LastIndexOf(Path.DirectorySeparatorChar + key + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase);
                        if (idx < 0)
                            idx = e.FullPath.LastIndexOf(Path.AltDirectorySeparatorChar + key + Path.AltDirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                            rootPath = e.FullPath[..(idx + 1 + key.Length)];
                        else
                            rootPath = Path.GetDirectoryName(e.FullPath) ?? e.FullPath;
                    }
                    catch { /* keep */ }

                    g = new SendGroup(key, isFolder: true, rootPath);
                    folderMap[key] = g;
                    result.Add(g);
                }

                g.Files.Add(e);
            }
            else
            {
                result.Add(new SendGroup(parts.Length > 0 ? parts[0] : e.RelativePath, isFolder: false, e.FullPath)
                {
                    Files = { e }
                });
            }
        }

        return result;
    }

    private async Task<bool> SendFileCoreAsync(
        TransferItem uiItem,
        Guid transferId,
        string fullPath,
        long totalBytes,
        string peer,
        long seq,
        int index,
        int count,
        string? targetDirectory,
        string? relativePath,
        bool directToPath,
        string? groupKey,
        int groupIndex,
        int groupCount,
        CancellationToken ct = default)
    {
        lock (_gate) _activeSends[uiItem.Id] = (peer, transferId);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!uiItem.IsFolderBatch)
                uiItem.State = TransferState.Transferring;

            var offer = MessageCodec.JsonBytes(new FileOfferPayload
            {
                TransferId = transferId,
                FileName = Path.GetFileName(fullPath),
                TotalBytes = totalBytes,
                Sequence = seq,
                FileIndex = index,
                FileCount = count,
                TargetDirectory = targetDirectory,
                RelativePath = relativePath ?? Path.GetFileName(fullPath),
                DirectToPath = directToPath,
                GroupKey = groupKey,
                GroupIndex = groupIndex,
                GroupCount = groupCount
            });
            if (!await _connections.SendToAsync(peer, MessageType.FileOffer, offer).ConfigureAwait(false))
                throw new IOException($"对端未连接: {peer}");

            await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[ChunkSize];
            long offset = 0;
            var lastUi = DateTime.UtcNow;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var n = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;

                var payload = new byte[24 + n];
                transferId.ToByteArray().CopyTo(payload, 0);
                BitConverter.TryWriteBytes(payload.AsSpan(16, 8), offset);
                Buffer.BlockCopy(buffer, 0, payload, 24, n);
                if (!await _connections.SendToAsync(peer, MessageType.FileChunk, payload).ConfigureAwait(false))
                    throw new IOException("传输中断：连接已断开");
                offset += n;
                if ((DateTime.UtcNow - lastUi).TotalMilliseconds >= 80 || offset >= totalBytes)
                {
                    uiItem.BytesTransferred = offset;
                    lastUi = DateTime.UtcNow;
                }
            }

            ct.ThrowIfCancellationRequested();
            var done = MessageCodec.JsonBytes(new FileControlPayload { TransferId = transferId });
            await _connections.SendToAsync(peer, MessageType.FileComplete, done).ConfigureAwait(false);
            uiItem.BytesTransferred = totalBytes;
            if (!uiItem.IsFolderBatch)
            {
                uiItem.State = TransferState.Completed;
                NotifyStatus($"已发送: {uiItem.FileName} → {peer}");
                QueueSaveHistory();
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                var abort = MessageCodec.JsonBytes(new FileControlPayload
                {
                    TransferId = transferId,
                    Reason = "用户取消"
                });
                await _connections.SendToAsync(peer, MessageType.FileAbort, abort).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            return false;
        }
        catch (Exception ex)
        {
            uiItem.State = TransferState.Failed;
            uiItem.Status = ex.Message;
            CompletePullWaiter(null);
            QueueSaveHistory();
            try
            {
                var abort = MessageCodec.JsonBytes(new FileControlPayload
                {
                    TransferId = transferId,
                    Reason = ex.Message
                });
                await _connections.SendToAsync(peer, MessageType.FileAbort, abort).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            return false;
        }
        finally
        {
            lock (_gate) _activeSends.Remove(uiItem.Id);
        }
    }

    private CancellationToken GetItemAbortToken(TransferItem item)
    {
        lock (_gate)
        {
            if (_itemAbort.TryGetValue(item.Id, out var existing))
            {
                try { existing.Cancel(); } catch { /* ignore */ }
                try { existing.Dispose(); } catch { /* ignore */ }
            }

            var cts = new CancellationTokenSource();
            _itemAbort[item.Id] = cts;
            return cts.Token;
        }
    }

    private void ClearItemAbort(Guid itemId)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_itemAbort.Remove(itemId, out cts)) return;
        }

        try { cts.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>中止该项进行中的收发，并通知对端。</summary>
    private void AbortItemTransfers(TransferItem item)
    {
        CancellationTokenSource? cts;
        (string Peer, Guid TransferId)? activeSend = null;
        List<ReceiveState> receives;
        lock (_gate)
        {
            _itemAbort.Remove(item.Id, out cts);
            if (_activeSends.TryGetValue(item.Id, out var send))
                activeSend = send;

            receives = _receiving.Values.Where(r => ReferenceEquals(r.Item, item)).ToList();
            foreach (var r in receives)
                _receiving.Remove(r.TransferId);

            var folderKeys = _folderUiItems.Where(kv => ReferenceEquals(kv.Value, item)).Select(kv => kv.Key).ToList();
            foreach (var k in folderKeys)
            {
                _folderUiItems.Remove(k);
                _folderUiCompleted.Remove(k);
            }
        }

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }

        if (activeSend is { } sendInfo)
        {
            try
            {
                var abort = MessageCodec.JsonBytes(new FileControlPayload
                {
                    TransferId = sendInfo.TransferId,
                    Reason = "用户取消"
                });
                _ = _connections.SendToAsync(sendInfo.Peer, MessageType.FileAbort, abort);
            }
            catch { /* ignore */ }
        }

        foreach (var state in receives)
        {
            try { state.Stream.Dispose(); } catch { /* ignore */ }
            try
            {
                if (File.Exists(state.DestPath))
                    File.Delete(state.DestPath);
            }
            catch { /* ignore */ }

            var peer = state.Item.PeerName;
            if (string.IsNullOrWhiteSpace(peer)) continue;
            try
            {
                var abort = MessageCodec.JsonBytes(new FileControlPayload
                {
                    TransferId = state.TransferId,
                    Reason = "用户取消"
                });
                _ = _connections.SendToAsync(peer, MessageType.FileAbort, abort);
            }
            catch { /* ignore */ }
        }

        CompletePullWaiter(null);
    }

    private void NotifyStatus(string message) => UiMarshal.Post(() => StatusMessage?.Invoke(message));

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
                EnqueueChunkWrite(msg.Payload);
                break;
            case MessageType.FileComplete:
                EnqueueFileComplete(msg.Payload);
                break;
            case MessageType.FileAbort:
                EnqueueFileAbort(msg.Payload);
                break;
            case MessageType.FileTransferRemove:
                HandleTransferRemove(peer, msg.Payload);
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
        await SendExpandedGroupsAsync(peer, seq, entries, targetDirectory, directToPath: true, ct)
            .ConfigureAwait(false);
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
                var added = 0;
                try
                {
                    foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        if (!seen.Add(file.FullName)) continue;
                        var rel = file.FullName.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                            ? Path.Combine(root.Name, file.FullName[rootPrefix.Length..])
                            : Path.Combine(root.Name, file.Name);
                        result.Add(new DirectEntry(file.FullName, rel, file.Length));
                        added++;
                    }
                }
                catch
                {
                    // 部分子项不可枚举时仍尽量发送已枚举部分
                }

                // 空文件夹：用临时哨兵文件传结构，勿写入用户源目录
                if (added == 0)
                {
                    var markerRel = Path.Combine(root.Name, ".flyscreen_dir");
                    try
                    {
                        var markerFull = Path.Combine(Path.GetTempPath(),
                            "flyscreen_dir_" + Guid.NewGuid().ToString("N"));
                        File.WriteAllBytes(markerFull, []);
                        if (seen.Add(markerFull))
                            result.Add(new DirectEntry(markerFull, markerRel, 0));
                    }
                    catch
                    {
                        // ignore
                    }
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
        if (announce == null || !_settings.TransferFile || !_settings.ShareClipboard)
        {
            NotifyStatus(announce == null
                ? "收到文件宣告但无法解析"
                : "收到文件宣告但未启用剪贴板/文件传输");
            return;
        }

        var list = announce.Files;
        if (list == null || list.Count == 0) return;

        var source = string.IsNullOrWhiteSpace(announce.SourceMachine) ? peer : announce.SourceMachine;
        lock (_gate)
        {
            _remotePending = new RemotePendingOffer(announce.Sequence, source, list.ToList());
            _remoteInlinePending = null; // 文件宣告覆盖未粘贴的内联内容
        }

        var dirs = list.Count(f => f.IsDirectory);
        var filesOnly = list.Count - dirs;
        var summary = dirs == 0 ? $"{filesOnly} 个文件"
            : filesOnly == 0 ? $"{dirs} 个文件夹"
            : $"{filesOnly} 文件+{dirs} 文件夹";
        NotifyStatus($"对端 {source} 已复制 {summary}，粘贴时传输");
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
                // 剪贴板拉取：按相对路径落盘，保留文件夹结构
                var rel = string.IsNullOrWhiteSpace(offer.RelativePath) ? offer.FileName : offer.RelativePath;
                displayName = rel.Replace('\\', '/');
                dest = ResolveDirectDestination(
                    Path.Combine(_inboxDir, $"seq_{offer.Sequence}"),
                    rel);
                var parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
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

        // 空目录哨兵不进传输列表
        var isDirMarker = string.Equals(Path.GetFileName(displayName), ".flyscreen_dir",
            StringComparison.OrdinalIgnoreCase);

        TransferItem item;
        var groupKey = offer.GroupKey;
        var groupCount = offer.GroupCount;
        // 兼容旧对端：无 Group 字段时，从相对路径推断文件夹
        if (string.IsNullOrWhiteSpace(groupKey) && groupCount <= 1)
        {
            var rel = (offer.RelativePath ?? displayName).Replace('\\', '/');
            var slash = rel.IndexOf('/');
            if (slash > 0)
            {
                groupKey = rel[..slash];
                groupCount = Math.Max(1, offer.FileCount);
            }
        }

        var isFolderBatch = !string.IsNullOrWhiteSpace(groupKey) && groupCount > 1;
        if (isFolderBatch)
        {
            item = GetOrCreateFolderUiItem(
                offer.Sequence, groupKey!, peer, dest, groupCount, offer.GroupIndex, offer.TotalBytes);
            if (isDirMarker)
            {
                // 仍接收落盘，但不刷新为“当前文件”
            }
            else
            {
                item.BatchIndex = offer.GroupIndex + 1;
                item.BatchCount = Math.Max(item.BatchCount, groupCount);
                item.TotalBytes = offer.TotalBytes;
                item.BytesTransferred = 0;
                item.State = TransferState.Transferring;
                item.BatchTotalBytes += offer.TotalBytes;
            }
        }
        else
        {
            item = new TransferItem
            {
                Id = offer.TransferId,
                Direction = TransferDirection.Receive,
                FileName = displayName,
                FullPath = dest,
                TotalBytes = offer.TotalBytes,
                PeerName = peer,
                Sequence = offer.Sequence,
                GroupKey = null,
                State = TransferState.Transferring,
                BytesTransferred = 0
            };
            if (!isDirMarker)
                AddItem(item);
        }

        var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read, ChunkSize, useAsync: true);
        lock (_gate)
        {
            _receiving[offer.TransferId] = new ReceiveState(
                offer.TransferId, item, fs, offer.Sequence, offer.DirectToPath, dest, isFolderBatch);
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
            : isFolderBatch
                ? $"接收文件夹: {groupKey} ({offer.GroupIndex + 1}/{groupCount}) ← {peer}"
                : $"接收中: {offer.FileName} ← {peer}");
    }

    private TransferItem GetOrCreateFolderUiItem(
        long sequence,
        string groupKey,
        string peer,
        string sampleDest,
        int groupCount,
        int groupIndex,
        long fileBytes)
    {
        var key = (sequence, groupKey);
        lock (_gate)
        {
            if (_folderUiItems.TryGetValue(key, out var existing))
                return existing;
        }

        string folderPath;
        try
        {
            var parent = Path.GetDirectoryName(sampleDest);
            folderPath = parent != null && Path.GetFileName(parent)
                .Equals(groupKey, StringComparison.OrdinalIgnoreCase)
                ? parent
                : sampleDest;
        }
        catch
        {
            folderPath = sampleDest;
        }

        var item = new TransferItem
        {
            Id = Guid.NewGuid(),
            Direction = TransferDirection.Receive,
            FileName = groupKey,
            FullPath = folderPath,
            PeerName = peer,
            Sequence = sequence,
            GroupKey = groupKey,
            State = TransferState.Transferring,
            BatchCount = Math.Max(1, groupCount),
            BatchIndex = groupIndex + 1,
            BatchTotalBytes = 0,
            TotalBytes = fileBytes,
            BytesTransferred = 0
        };
        AddItem(item);
        lock (_gate)
        {
            _folderUiItems[key] = item;
            _folderUiCompleted[key] = 0;
        }

        return item;
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

    private void EnqueueChunkWrite(byte[] payload)
    {
        if (payload.Length < 24) return;
        var id = new Guid(payload.AsSpan(0, 16));
        var offset = BitConverter.ToInt64(payload, 16);
        var dataLen = payload.Length - 24;
        var data = new byte[dataLen];
        Buffer.BlockCopy(payload, 24, data, 0, dataLen);
        _chunkDiskChannel.Writer.TryWrite(new ChunkDiskJob.Write(id, offset, data));
    }

    private void EnqueueFileComplete(byte[] payload)
        => _chunkDiskChannel.Writer.TryWrite(new ChunkDiskJob.Complete(payload));

    private void EnqueueFileAbort(byte[] payload)
        => _chunkDiskChannel.Writer.TryWrite(new ChunkDiskJob.Abort(payload));

    private async Task ChunkDiskLoopAsync()
    {
        try
        {
            await foreach (var job in _chunkDiskChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (job)
                {
                    case ChunkDiskJob.Write w:
                        WriteChunkToDisk(w.Id, w.Offset, w.Data);
                        break;
                    case ChunkDiskJob.Complete c:
                        await HandleCompleteAsync(c.Payload).ConfigureAwait(false);
                        break;
                    case ChunkDiskJob.Abort a:
                        HandleAbort(a.Payload);
                        break;
                }
            }
        }
        catch
        {
            // 进程退出时忽略
        }
    }

    private void WriteChunkToDisk(Guid id, long offset, byte[] data)
    {
        ReceiveState? state;
        lock (_gate) _receiving.TryGetValue(id, out state);
        if (state == null) return;

        try
        {
            state.Stream.Position = offset;
            state.Stream.Write(data, 0, data.Length);
            var transferred = Math.Max(state.Item.BytesTransferred, offset + data.Length);
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

            if (state.IsFolderBatch)
            {
                var gKey = state.Item.FileName;
                var fk = (state.Sequence, gKey);
                int done;
                lock (_gate)
                {
                    _folderUiCompleted.TryGetValue(fk, out done);
                    done++;
                    _folderUiCompleted[fk] = done;
                }

                if (done >= state.Item.BatchCount)
                {
                    state.Item.BytesTransferred = state.Item.BatchTotalBytes > 0
                        ? state.Item.BatchTotalBytes
                        : state.Item.TotalBytes;
                    state.Item.TotalBytes = state.Item.BytesTransferred;
                    state.Item.State = TransferState.Completed;
                    lock (_gate)
                    {
                        _folderUiItems.Remove(fk);
                        _folderUiCompleted.Remove(fk);
                    }
                }
                else
                {
                    // 保持 Transferring，Status 仍为 n/N
                    state.Item.BatchIndex = Math.Min(done + 1, state.Item.BatchCount);
                }
            }
            else
            {
                state.Item.State = TransferState.Completed;
            }

            bool ready;
            List<string> files;
            lock (_gate)
            {
                if (!_batches.TryGetValue(state.Sequence, out var batch))
                {
                    batch = new BatchReceive(state.Sequence, 1);
                    _batches[state.Sequence] = batch;
                }

                batch.CompletedPaths.Add(state.DestPath);
                ready = batch.CompletedPaths.Count >= batch.ExpectedCount
                        && !_receiving.Values.Any(r => r.Sequence == state.Sequence);
                files = ready ? batch.CompletedPaths.ToList() : [];
                if (ready)
                {
                    _batches.Remove(state.Sequence);
                    // 清理本序列残留的文件夹 UI 索引
                    var stale = _folderUiItems.Keys.Where(k => k.Seq == state.Sequence).ToList();
                    foreach (var k in stale)
                    {
                        _folderUiItems.Remove(k);
                        _folderUiCompleted.Remove(k);
                    }
                }
            }

            QueueSaveHistory();

            if (ready)
            {
                if (state.IsDirect)
                    NotifyStatus($"直接传输完成（{files.Count} 个文件）");
                else
                    FinishPulledBatch(state.Sequence, files);
            }

            NotifyStatus(state.IsFolderBatch
                ? $"已接收: {state.Item.FileName}（文件）"
                : $"已接收: {state.Item.FileName}");
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

        // 对端中止时，若本机正在发送同一 TransferId，取消发送循环
        List<CancellationTokenSource> cancelSends = [];
        lock (_gate)
        {
            foreach (var kv in _activeSends)
            {
                if (kv.Value.TransferId != ctrl.TransferId) continue;
                if (_itemAbort.TryGetValue(kv.Key, out var cts))
                    cancelSends.Add(cts);
            }
        }

        foreach (var cts in cancelSends)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
        }

        ReceiveState? state;
        lock (_gate) _receiving.Remove(ctrl.TransferId, out state);
        if (state == null) return;
        try { state.Stream.Dispose(); } catch { /* ignore */ }
        try { if (File.Exists(state.DestPath)) File.Delete(state.DestPath); } catch { /* ignore */ }
        // 本地已点删除移出列表时不再改状态
        if (Items.Contains(state.Item))
        {
            state.Item.State = TransferState.Failed;
            state.Item.Status = ctrl.Reason ?? "对端中止";
        }

        CompletePullWaiter(null);
        QueueSaveHistory();
    }

    private void HandleClipboardText(byte[] payload)
    {
        var p = MessageCodec.DecodeJson<ClipboardTextPayload>(payload);
        if (p == null || !_settings.ShareClipboard) return;
        if (p.Sequence <= _lastAppliedRemoteSequence) return;
        _lastAppliedRemoteSequence = p.Sequence;

        UiMarshal.Post(() =>
        {
            try { _clipboard.ApplyText(p.Text); }
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
        lock (_gate)
        {
            _lastPasteReadyPaths = pastePaths.Count > 0 ? pastePaths.ToArray() : null;
            // _lastPasteEntries 由 PreparePastePaths 写入
        }
        NotifyStatus($"文件接收完成（{pastePaths.Count} 个）");
        CompletePullWaiter(pastePaths.Count > 0 ? pastePaths : null);
    }

    /// <summary>
    /// 将收件箱缓存映射为原相对路径结构，供 CF_HDROP 粘贴（含文件夹）。
    /// 若已 <see cref="SetPasteMergeRoots"/>，则直接写入占位路径（文件夹内容填满后再粘贴）。
    /// </summary>
    private List<string> PreparePastePaths(IReadOnlyList<string> inboxPaths)
    {
        try
        {
            Directory.CreateDirectory(_pasteDir);

            string[]? mergeRoots;
            lock (_gate) mergeRoots = _pasteMergeRoots;

            var rootByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string batchDir;
            if (mergeRoots is { Length: > 0 })
            {
                foreach (var r in mergeRoots)
                {
                    var name = Path.GetFileName(
                        r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(name))
                        rootByName[name] = r;
                }

                batchDir = Path.GetDirectoryName(mergeRoots[0]) ?? _pasteDir;
            }
            else
            {
                batchDir = Path.Combine(_pasteDir, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(batchDir);
            }

            var topLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pasteEntries = new List<ClipboardPasteEntry>();
            var dirEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void NoteDir(string relDir, string fullDir)
            {
                var key = relDir.Replace('/', '\\').TrimEnd('\\');
                if (string.IsNullOrEmpty(key) || !dirEntries.Add(key)) return;
                pasteEntries.Add(new ClipboardPasteEntry(key, fullDir, isDirectory: true, size: 0));
            }

            foreach (var inboxPath in inboxPaths)
            {
                if (!File.Exists(inboxPath)) continue;

                var relative = ResolveRelativePasteName(inboxPath);
                // 接收端不保留目录占位哨兵文件
                if (relative.EndsWith("/.flyscreen_dir", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals(".flyscreen_dir", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith("\\.flyscreen_dir", StringComparison.OrdinalIgnoreCase))
                {
                    var dirRel = relative.Replace('\\', '/');
                    var slash = dirRel.LastIndexOf('/');
                    var folderName = slash > 0 ? dirRel[..slash] : "";
                    if (!string.IsNullOrEmpty(folderName)
                        && rootByName.TryGetValue(folderName.Split('/')[0], out var folderRoot))
                    {
                        var segs = folderName.Split('/');
                        var dirPath = segs.Length == 1
                            ? folderRoot
                            : Path.Combine(folderRoot, Path.Combine(segs.Skip(1).ToArray()));
                        Directory.CreateDirectory(dirPath);
                        topLevels.Add(folderRoot);
                        NoteAncestorDirs(segs, folderRoot, isMergeRoot: true);
                    }
                    else if (!string.IsNullOrEmpty(folderName))
                    {
                        var segs = folderName.Split('/');
                        var dirPath = Path.Combine(batchDir, Path.Combine(segs));
                        Directory.CreateDirectory(dirPath);
                        topLevels.Add(Path.Combine(batchDir, segs[0]));
                        NoteAncestorDirs(segs, batchDir, isMergeRoot: false);
                    }

                    continue;
                }

                var segments = relative.Replace('/', Path.DirectorySeparatorChar)
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
                    continue;

                string dest;
                string top;
                if (rootByName.TryGetValue(segments[0], out var root))
                {
                    top = root;
                    dest = segments.Length == 1
                        ? root
                        : Path.Combine(root, Path.Combine(segments.Skip(1).ToArray()));
                    // 虚拟剪贴板需要中间目录条目，否则 Explorer 会丢层级
                    if (segments.Length > 1)
                        NoteAncestorDirs(segments[..^1], root, isMergeRoot: true);
                }
                else
                {
                    top = Path.Combine(batchDir, segments[0]);
                    dest = Path.Combine(batchDir, Path.Combine(segments));
                    // 仅登记祖先目录；单文件本身不是目录
                    if (segments.Length > 1)
                        NoteAncestorDirs(segments[..^1], batchDir, isMergeRoot: false);
                }

                var parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                if (!LinkOrCopy(inboxPath, dest))
                {
                    NotifyStatus($"写入粘贴文件失败: {relative}");
                    continue;
                }

                RelocateTransferItemPath(inboxPath, dest);
                topLevels.Add(top);

                var relWin = string.Join("\\", segments);
                long size = 0;
                try { size = new FileInfo(dest).Length; } catch { /* ignore */ }
                pasteEntries.Add(new ClipboardPasteEntry(relWin, dest, isDirectory: false, size));
            }

            // 局部：为 FileGroupDescriptor 登记目录祖先（保留文件夹层级）
            void NoteAncestorDirs(string[] dirSegments, string basePath, bool isMergeRoot)
            {
                if (dirSegments.Length == 0) return;
                for (var i = 1; i <= dirSegments.Length; i++)
                {
                    var segs = dirSegments.Take(i).ToArray();
                    var rel = string.Join("\\", segs);
                    string full;
                    if (isMergeRoot)
                    {
                        full = segs.Length == 1
                            ? basePath
                            : Path.Combine(basePath, Path.Combine(segs.Skip(1).ToArray()));
                    }
                    else
                    {
                        full = Path.Combine(basePath, Path.Combine(segs));
                    }

                    try { Directory.CreateDirectory(full); } catch { /* ignore */ }
                    NoteDir(rel, full);
                }
            }

            List<string> result;
            if (mergeRoots is { Length: > 0 })
                result = mergeRoots.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            else
                result = topLevels.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

            lock (_gate)
                _lastPasteEntries = pasteEntries.Count > 0 ? pasteEntries.ToArray() : null;

            if (result.Count == 0)
                NotifyStatus("准备粘贴路径为空（内容可能未写入）");

            return result;
        }
        catch (Exception ex)
        {
            NotifyStatus("准备粘贴文件失败: " + ex.Message);
            return inboxPaths.Where(File.Exists).ToList();
        }
        finally
        {
            lock (_gate) _pasteMergeRoots = null;
        }
    }

    /// <summary>粘贴就绪后把列表项路径改到实际可打开的文件（PasteStaging）。</summary>
    private void RelocateTransferItemPath(string fromPath, string toPath)
    {
        if (string.IsNullOrWhiteSpace(fromPath) || string.IsNullOrWhiteSpace(toPath)) return;
        if (string.Equals(fromPath, toPath, StringComparison.OrdinalIgnoreCase)) return;

        UiMarshal.Post(() =>
        {
            var item = Items.FirstOrDefault(i =>
                string.Equals(i.FullPath, fromPath, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            item.FullPath = toPath;
            QueueSaveHistory();
        });
    }

    /// <summary>
    /// 从收件箱绝对路径还原相对路径（含文件夹层级）。
    /// 文件夹批量时列表项 FileName 只有根名，不可用来还原子路径。
    /// </summary>
    private string ResolveRelativePasteName(string inboxPath)
    {
        try
        {
            var full = Path.GetFullPath(inboxPath);
            var inboxRoot = Path.GetFullPath(_inboxDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (full.StartsWith(inboxRoot, StringComparison.OrdinalIgnoreCase))
            {
                var rest = full[inboxRoot.Length..]
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var parts = rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                // Inbox/seq_{n}/Folder/sub/file.txt → Folder/sub/file.txt
                if (parts.Length >= 2
                    && parts[0].StartsWith("seq_", StringComparison.OrdinalIgnoreCase))
                    return string.Join('/', parts.Skip(1));
                if (parts.Length >= 1)
                    return string.Join('/', parts);
            }
        }
        catch { /* ignore */ }

        // 单文件：列表项 FileName 可能带相对路径
        try
        {
            var snapshot = Items.ToArray();
            var item = snapshot.FirstOrDefault(i =>
                string.Equals(i.FullPath, inboxPath, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(item?.FileName)
                && (item.FileName.Contains('/') || item.FileName.Contains('\\')))
                return item.FileName.Replace('\\', '/');
            // 仅当不是文件夹批量行时，才用 FileName 作为单文件名
            if (!string.IsNullOrWhiteSpace(item?.FileName) && !item.IsFolderBatch)
                return item.FileName.Replace('\\', '/');
        }
        catch { /* ignore */ }

        var name = Path.GetFileName(inboxPath);
        var m = InboxNameRegex.Match(name);
        return m.Success ? m.Groups["name"].Value : name;
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
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dest))
                {
                    // 不应把文件写到目录路径上
                    return false;
                }

                if (File.Exists(dest))
                    File.Delete(dest);

                File.Copy(source, dest, overwrite: true);
                if (!File.Exists(dest)) return false;

                var srcLen = new FileInfo(source).Length;
                var dstLen = new FileInfo(dest).Length;
                if (srcLen == dstLen) return true;
            }
            catch
            {
                // 剪贴板占用占位文件时短暂重试
                Thread.Sleep(40 + attempt * 30);
            }
        }

        return false;
    }

    public async Task BroadcastTextAsync(string text)
    {
        if (!_settings.ShareClipboard || string.IsNullOrEmpty(text)) return;
        var seq = NextSequence();
        var payload = MessageCodec.JsonBytes(new ClipboardTextPayload { Text = text, Sequence = seq });
        await _connections.BroadcastAsync(MessageType.ClipboardText, payload).ConfigureAwait(false);
    }

    /// <summary>
    /// 清除已完成/失败/取消记录（仅本机，不同步对端）。
    /// <paramref name="deleteReceiveFiles"/> 为 true 时删除本机接收文件；发送项永不删源文件。
    /// </summary>
    public void ClearCompleted(bool deleteReceiveFiles = true)
    {
        List<TransferItem> done = [];
        UiMarshal.Send(() =>
        {
            done = Items
                .Where(i => i.State is TransferState.Completed or TransferState.Cancelled or TransferState.Failed)
                .ToList();
        });

        var removedReceiveCache = 0;
        foreach (var item in done)
        {
            var delFiles = deleteReceiveFiles && item.Direction == TransferDirection.Receive;
            if (delFiles && TryDeleteReceiveCache(item))
                removedReceiveCache++;
            UiMarshal.Send(() => Items.Remove(item));
        }

        if (deleteReceiveFiles)
            CleanupPasteDir();
        SaveHistoryNow();
        NotifyStatus(removedReceiveCache > 0
            ? $"已清除记录，并删除 {removedReceiveCache} 个接收文件"
            : "已清除记录（未删除本地接收文件 / 发送项未改动源文件）");
    }

    /// <summary>
    /// 删除单条记录。传输中会中断并同步对端删除；已完成仅删本机记录（可选删接收文件），不同步对端。
    /// </summary>
    public void RemoveItem(TransferItem item, bool deleteCache = true)
    {
        var active = item.State is TransferState.Queued or TransferState.Transferring;
        // 仅传输中同步对端；已完成各自删自己的记录
        RemoveItemCore(item, deleteCache, notifyPeer: active, statusPrefix: null);
    }

    private void RemoveItemCore(
        TransferItem item,
        bool deleteCache,
        bool notifyPeer,
        string? statusPrefix)
    {
        var wasActive = item.State is TransferState.Queued or TransferState.Transferring;
        if (notifyPeer && wasActive)
            NotifyPeerRemove(item);

        if (wasActive)
            AbortItemTransfers(item);

        var deletedCache = false;
        UiMarshal.Send(() =>
        {
            if (deleteCache)
                deletedCache = TryDeleteReceiveCache(item);
            Items.Remove(item);
        });
        SaveHistoryNow();

        var name = item.FileName;
        var prefix = string.IsNullOrEmpty(statusPrefix) ? "" : statusPrefix;
        if (wasActive)
            NotifyStatus(deletedCache
                ? $"{prefix}已中断传输并删除接收文件: {name}"
                : $"{prefix}已中断传输并删除记录: {name}");
        else
            NotifyStatus(deletedCache
                ? $"{prefix}已删除记录及接收文件: {name}"
                : $"{prefix}已删除记录: {name}");
    }

    private void NotifyPeerRemove(TransferItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PeerName)) return;
        try
        {
            var groupKey = item.GroupKey;
            if (string.IsNullOrWhiteSpace(groupKey) && item.IsFolderBatch)
                groupKey = item.FileName;

            var payload = MessageCodec.JsonBytes(new FileTransferRemovePayload
            {
                Sequence = item.Sequence,
                GroupKey = groupKey,
                TransferId = item.IsFolderBatch || !string.IsNullOrWhiteSpace(groupKey)
                    ? Guid.Empty
                    : item.Id,
                FileName = item.FileName
            });
            _ = _connections.SendToAsync(item.PeerName, MessageType.FileTransferRemove, payload);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 对端在「传输中」删除：本机同步中断并删记录/未完成文件。
    /// 已完成项忽略（完成态不同步删除）。
    /// </summary>
    private void HandleTransferRemove(string peer, byte[] payload)
    {
        var p = MessageCodec.DecodeJson<FileTransferRemovePayload>(payload);
        if (p == null) return;

        TransferItem? item = null;
        UiMarshal.Send(() => item = FindLinkedTransferItem(peer, p));
        if (item != null)
        {
            // 仅同步删除仍在传输中的项
            if (item.State is not (TransferState.Queued or TransferState.Transferring))
                return;

            RemoveItemCore(item, deleteCache: true, notifyPeer: false, statusPrefix: "对端同步：");
            return;
        }

        // 列表项已不在，仍清理进行中的接收落盘
        CleanupOrphanReceivesForRemove(peer, p);
    }

    private TransferItem? FindLinkedTransferItem(string peer, FileTransferRemovePayload p)
    {
        bool PeerMatch(TransferItem i) =>
            string.Equals(i.PeerName, peer, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(p.GroupKey))
        {
            var hit = Items.FirstOrDefault(i =>
                PeerMatch(i)
                && (p.Sequence == 0 || i.Sequence == p.Sequence)
                && (string.Equals(i.GroupKey, p.GroupKey, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(i.FileName, p.GroupKey, StringComparison.OrdinalIgnoreCase)));
            if (hit != null) return hit;
        }

        if (p.TransferId != Guid.Empty)
        {
            var byId = Items.FirstOrDefault(i => PeerMatch(i) && i.Id == p.TransferId);
            if (byId != null) return byId;
        }

        if (p.Sequence != 0 && !string.IsNullOrWhiteSpace(p.FileName))
        {
            return Items.FirstOrDefault(i =>
                PeerMatch(i)
                && i.Sequence == p.Sequence
                && string.Equals(i.FileName, p.FileName, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private void CleanupOrphanReceivesForRemove(string peer, FileTransferRemovePayload p)
    {
        List<ReceiveState> receives;
        lock (_gate)
        {
            receives = _receiving.Values
                .Where(r => string.Equals(r.Item.PeerName, peer, StringComparison.OrdinalIgnoreCase)
                    && (p.Sequence == 0 || r.Sequence == p.Sequence)
                    && (string.IsNullOrWhiteSpace(p.GroupKey)
                        || string.Equals(r.Item.GroupKey ?? r.Item.FileName, p.GroupKey,
                            StringComparison.OrdinalIgnoreCase)
                        || r.TransferId == p.TransferId))
                .ToList();
            foreach (var r in receives)
                _receiving.Remove(r.TransferId);
        }

        foreach (var state in receives)
        {
            try { state.Stream.Dispose(); } catch { /* ignore */ }
            try
            {
                if (File.Exists(state.DestPath))
                    File.Delete(state.DestPath);
            }
            catch { /* ignore */ }
        }

        if (receives.Count > 0)
            CompletePullWaiter(null);
    }

    /// <summary>仅删除接收方向的已收文件/文件夹；发送路径一律不动。</summary>
    private bool TryDeleteReceiveCache(TransferItem item)
    {
        // 传出历史：FullPath 是用户本机源文件，禁止删除
        if (item.Direction != TransferDirection.Receive)
            return false;
        if (string.IsNullOrWhiteSpace(item.FullPath))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(item.FullPath);
        }
        catch
        {
            return false;
        }

        // 拒绝盘符根等危险路径
        try
        {
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root)
                && string.Equals(
                    full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
                return true;
            }

            if (File.Exists(full))
            {
                File.Delete(full);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
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
        UiMarshal.Post(() =>
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
                    Sequence = e.Sequence,
                    GroupKey = e.GroupKey,
                    CreatedAt = e.CreatedAt == default ? DateTime.Now : e.CreatedAt,
                    State = e.State,
                    BatchCount = e.BatchCount,
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
            UiMarshal.Send(() =>
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
                        Sequence = i.Sequence,
                        GroupKey = i.GroupKey,
                        BatchCount = i.BatchCount,
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

    private abstract record ChunkDiskJob
    {
        public sealed record Write(Guid Id, long Offset, byte[] Data) : ChunkDiskJob;
        public sealed record Complete(byte[] Payload) : ChunkDiskJob;
        public sealed record Abort(byte[] Payload) : ChunkDiskJob;
    }

    private sealed class TransferHistoryDto
    {
        public Guid Id { get; set; }
        public TransferDirection Direction { get; set; }
        public string? FileName { get; set; }
        public string? FullPath { get; set; }
        public long TotalBytes { get; set; }
        public string? PeerName { get; set; }
        public long Sequence { get; set; }
        public string? GroupKey { get; set; }
        public int BatchCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public TransferState State { get; set; }
    }

    private sealed class LocalPendingFile(string fullPath, string fileName, long totalBytes, bool isDirectory)
    {
        public string FullPath { get; } = fullPath;
        public string FileName { get; } = fileName;
        public long TotalBytes { get; } = totalBytes;
        public bool IsDirectory { get; } = isDirectory;
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

    private sealed class ReceiveState(
        Guid transferId,
        TransferItem item,
        FileStream stream,
        long sequence,
        bool isDirect,
        string destPath,
        bool isFolderBatch)
    {
        public Guid TransferId { get; } = transferId;
        public TransferItem Item { get; } = item;
        public FileStream Stream { get; } = stream;
        public long Sequence { get; } = sequence;
        public bool IsDirect { get; } = isDirect;
        public string DestPath { get; } = destPath;
        public bool IsFolderBatch { get; } = isFolderBatch;
        public DateTime LastUiUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class DirectEntry(string fullPath, string relativePath, long totalBytes)
    {
        public string FullPath { get; } = fullPath;
        public string RelativePath { get; } = relativePath;
        public long TotalBytes { get; } = totalBytes;
    }

    private sealed class SendGroup(string key, bool isFolder, string rootPath)
    {
        public string Key { get; } = key;
        public bool IsFolder { get; } = isFolder;
        public string RootPath { get; } = rootPath;
        public List<DirectEntry> Files { get; } = [];
    }

    private sealed class BatchReceive(long sequence, int expectedCount)
    {
        public long Sequence { get; } = sequence;
        public int ExpectedCount { get; set; } = expectedCount;
        public List<string> CompletedPaths { get; } = [];
    }
}
