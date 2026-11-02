using FlyScreen.Helpers;
using FlyScreen.Models;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 拖文件穿边：源端拿着路径，光标按布局穿过多台机器。
/// 松开左键时，若停在另一台可放下的目录上，才开始把文件复制过去。
/// </summary>
/// <summary>松开时目标里已有同名项，用户的选择。</summary>
public enum EdgeDragConflictAction
{
    /// <summary>没有同名，或对端未询问，全部传输。</summary>
    Proceed = 0,
    Replace = 1,
    Skip = 2,
    Cancel = 3
}

/// <summary>接收端弹出「替换或跳过」时用的内容。</summary>
public sealed class EdgeDragConflictRequest
{
    public string Directory { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public int TotalCount { get; init; }
    public IReadOnlyList<EdgeDragItemInfo> SourceItems { get; init; } = [];
}

public sealed class EdgeFileDragService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly FileTransferService _transfers;
    private readonly Func<string> _resolveDropDirectory;
    private readonly object _gate = new();
    private int _phase;
    private string _session = "";
    private string[] _paths = [];
    private string[] _names = [];
    private string? _hoverPeer;
    private string? _cuePeer;
    private string _shownCueSession = "";
    private string? _awaitingPeer;
    private TaskCompletionSource<EdgeDragPlacePayload?>? _placeTcs;
    private int _followGeneration;

    public EdgeFileDragService(
        AppSettings settings,
        ConnectionManager connections,
        FileTransferService transfers,
        Func<string> resolveDropDirectory)
    {
        _settings = settings;
        _connections = connections;
        _transfers = transfers;
        _resolveDropDirectory = resolveDropDirectory;
        _connections.MessageReceived += OnMessage;
        _connections.PeerLinkChanged += OnPeerLinkChanged;
    }

    public event Action<string>? StatusMessage;

    /// <summary>本机正作为拖放目标时，画出或收起跟手提示。null 表示收起。</summary>
    public event Action<EdgeDragCue?>? CueChanged;

    public bool IsActive
    {
        get { lock (_gate) return _phase != 0; }
    }

    /// <summary>扩展屏占用的对端不接收穿边拖拽。</summary>
    public Func<string, bool>? RejectPeer { get; set; }

    /// <summary>
    /// 目标目录里已有同名文件或文件夹时，在那台电脑上询问替换还是跳过。
    /// 未设置时直接覆盖。
    /// </summary>
    public Func<EdgeDragConflictRequest, Task<EdgeDragConflictAction>>? ConfirmConflict { get; set; }

    /// <summary>左键仍按着、还在拖。松开后等待替换确认时为 false，方便去点对话框。</summary>
    public bool IsHolding
    {
        get { lock (_gate) return _phase == 1; }
    }

    public bool CanAccept(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return false;
        if (IsSelf(peer)) return false;
        if (RejectPeer?.Invoke(peer) == true) return false;
        return _connections.IsPeerEdgeMouseCapable(peer)
               && _connections.PeerSupports(peer, PeerCaps.InjectInput);
    }

    /// <summary>开始一次拖拽。已经在拖时返回 false，避免重复取消本机拖放。</summary>
    public bool TryBegin(IReadOnlyList<string> paths)
    {
        if (!_settings.TransferFile) return false;
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (list.Length == 0) return false;
        lock (_gate)
        {
            if (_phase != 0) return false;
            _phase = 1;
            _session = Guid.NewGuid().ToString("N");
            _paths = list;
            _names = DisplayNames(list);
            _hoverPeer = null;
            _cuePeer = null;
            _awaitingPeer = null;
        }

        Report($"正在拖 {list.Length} 项");
        NotifyActive(true);
        return true;
    }

    public void SetHover(string? peerOrNullIfLocal)
    {
        var peer = string.IsNullOrWhiteSpace(peerOrNullIfLocal) ? null : peerOrNullIfLocal;
        string? previous;
        string session;
        string[] names;
        int count;
        lock (_gate)
        {
            if (_phase != 1) return;
            _hoverPeer = peer;
            previous = _cuePeer;
            if (SamePeer(previous, peer)) return;
            _cuePeer = peer;
            session = _session;
            names = _names;
            count = _paths.Length;
        }

        if (!string.IsNullOrEmpty(previous) && !IsSelf(previous))
            _ = SendHoverAsync(previous, session, false, 0, []);
        if (!string.IsNullOrEmpty(peer) && !IsSelf(peer))
            _ = SendHoverAsync(peer, session, true, count, names);
    }

    /// <summary>
    /// 从本机某一边把已经拿到的文件交出去，并在能读指针时继续跟到布局里的下一台。
    /// </summary>
    public void OfferFromEdge(IReadOnlyList<string> paths, int exitSide, IEdgeDragPointer? follow)
    {
        if (!TryBegin(paths)) return;
        var next = LayoutEdge.Neighbor(_settings, LocalPeerIdentity.MachineName, exitSide);
        if (!CanAccept(next))
        {
            Cancel();
            return;
        }

        var (entryX, entryY) = EntryPoint(exitSide);
        SetHover(next);
        AnnounceOwner(next!);
        _ = SendSwitchAsync(next!, entryX, entryY);
        if (follow == null)
        {
            Finish();
            return;
        }

        var generation = Interlocked.Increment(ref _followGeneration);
        _ = FollowAsync(follow, next!, entryX, entryY, exitSide, generation);
    }

    public void Finish()
    {
        string? hover;
        string session;
        string[] paths;
        lock (_gate)
        {
            if (_phase != 1) return;
            _phase = 2;
            hover = _hoverPeer;
            session = _session;
            paths = _paths;
        }

        EndCue();

        if (string.IsNullOrWhiteSpace(hover) || IsSelf(hover))
        {
            Report("已回到本机，取消拖放");
            Clear();
            return;
        }

        var tcs = new TaskCompletionSource<EdgeDragPlacePayload?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _awaitingPeer = hover;
            _placeTcs = tcs;
        }

        _ = SendQueryAsync(hover, session, paths, tcs);
    }

    public void Cancel()
    {
        Interlocked.Increment(ref _followGeneration);
        Clear();
    }

    public void Dispose()
    {
        _connections.MessageReceived -= OnMessage;
        _connections.PeerLinkChanged -= OnPeerLinkChanged;
        Cancel();
    }

    private async Task FollowAsync(
        IEdgeDragPointer pointer, string peer, double entryX, double entryY, int peerSide, int generation)
    {
        try
        {
            pointer.DismissLocalDrag();
            pointer.WarpToCenter();
            await Task.Delay(30).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _followGeneration)) return;
            if (!await WaitUntilCenteredAsync(pointer, generation).ConfigureAwait(false))
            {
                Finish();
                return;
            }

            if (!pointer.TryRead(out var lastX, out var lastY, out var down) || !down)
            {
                Finish();
                return;
            }

            var cursor = new EdgeMouseVirtualCursor
            {
                EntryStickyEnabled = _settings.EdgeCrossMode is 1 or 2
            };
            cursor.BeginRemote(entryX, entryY, peerSide);
            var gate = new EdgeCrossGate
            {
                Mode = _settings.EdgeCrossMode switch
                {
                    1 => EdgeCrossMode.DoubleTap,
                    2 => EdgeCrossMode.SpeedBurst,
                    _ => EdgeCrossMode.BarrierFree
                }
            };
            var current = peer;

            while (generation == Volatile.Read(ref _followGeneration))
            {
                await Task.Delay(16).ConfigureAwait(false);
                if (!pointer.TryRead(out var x, out var y, out down))
                    break;
                if (!down)
                    break;

                pointer.ScreenSize(out var sw, out var sh);
                var dx = x - lastX;
                var dy = y - lastY;
                lastX = x;
                lastY = y;

                if (string.IsNullOrEmpty(current))
                {
                    var localExit = LocalExit(x, y, sw, sh);
                    if (localExit != 0)
                        TryHop(localExit, ref current, cursor);
                    continue;
                }

                if (Math.Abs(x - sw / 2) > sw / 5 || Math.Abs(y - sh / 2) > sh / 5)
                {
                    pointer.WarpToCenter();
                    if (pointer.TryRead(out lastX, out lastY, out _)) { }
                }

                if (!cursor.TryApplyPixelDelta(dx, dy, sw, sh))
                    continue;

                var (nx, ny) = cursor.Clamped01();
                _ = SendMoveAsync(current, nx, ny);

                if (!cursor.TryGetExitDirection(dx, dy, _settings.TwoRow, out var dirH, out var dirV))
                    continue;
                var exit = dirH != 0 ? dirH : dirV;
                var now = Environment.TickCount64;
                if (!gate.TryAdmitRemoteExit(exit, nx, ny, dx, dy, now))
                {
                    if (gate.ShouldClampVirtualCursorOnReject)
                        cursor.ClampInside();
                    continue;
                }

                TryHop(exit, ref current, cursor);
            }
        }
        catch
        {
            // 跟指针失败时仍尝试在当前机器放下
        }

        if (generation == Volatile.Read(ref _followGeneration))
            Finish();
    }

    private void OnPeerLinkChanged(string peer, bool outboundAlive, bool inboundAlive)
    {
        if (outboundAlive || inboundAlive) return;
        string? waiting;
        lock (_gate)
        {
            if (_phase == 0) return;
            waiting = _phase == 2 ? _awaitingPeer : _hoverPeer;
        }

        if (waiting == null || !string.Equals(waiting, peer, StringComparison.OrdinalIgnoreCase))
            return;
        Report("对端已断开，取消拖放");
        Cancel();
    }

    private void OnMessage(string peer, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.EdgeDragQuery:
                ReplyToQuery(peer, msg.Payload);
                break;
            case MessageType.EdgeDragPlace:
                AcceptPlace(peer, msg.Payload);
                break;
            case MessageType.EdgeDragHover:
                ApplyHover(msg.Payload);
                break;
        }
    }

    private void ReplyToQuery(string peer, byte[] payload)
    {
        var query = MessageCodec.DecodeJson<EdgeDragQueryPayload>(payload);
        if (query == null || string.IsNullOrEmpty(query.SessionId)) return;
        if (!_settings.TransferFile) return;
        // 替换确认会停在界面上。不能占着收包线程，否则心跳和鼠标都过不去。
        _ = Task.Run(() => ReplyToQueryAsync(peer, query));
    }

    private async Task ReplyToQueryAsync(string peer, EdgeDragQueryPayload query)
    {
        string dir;
        try { dir = _resolveDropDirectory() ?? ""; }
        catch { dir = ""; }

        var action = EdgeDragConflictAction.Proceed;
        var skip = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var conflicts = ConflictingNames(dir, query.Names);
            if (conflicts.Count > 0)
            {
                var confirm = ConfirmConflict;
                if (confirm == null)
                {
                    action = EdgeDragConflictAction.Replace;
                }
                else
                {
                    try
                    {
                        var request = new EdgeDragConflictRequest
                        {
                            Directory = dir,
                            SourceFolder = query.SourceFolder ?? "",
                            Conflicts = conflicts,
                            TotalCount = Math.Max(query.Names?.Length ?? 0, conflicts.Count),
                            SourceItems = query.Items ?? []
                        };
                        action = await confirm(request).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 弹窗异常时不要悄悄取消；记日志，默认走替换以免「没弹窗却取消」
                        try { global::FlyScreen.Services.AppLog.Error(ex, "替换确认弹窗失败"); } catch { /* ignore */ }
                        action = EdgeDragConflictAction.Replace;
                    }
                }
                if (action == EdgeDragConflictAction.Skip)
                    skip = conflicts.ToArray();
            }
        }

        try
        {
            await _connections.SendToAsync(peer, MessageType.EdgeDragPlace,
                MessageCodec.JsonBytes(new EdgeDragPlacePayload
                {
                    SessionId = query.SessionId,
                    Directory = dir,
                    Action = (int)action,
                    SkipNames = skip
                })).ConfigureAwait(false);
        }
        catch
        {
            // 答复失败时源端按超时取消
        }
    }

    private static List<string> ConflictingNames(string directory, string[]? names)
    {
        var list = new List<string>();
        if (names == null || names.Length == 0) return list;
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            string path;
            try { path = Path.Combine(directory, name); }
            catch { continue; }
            if (File.Exists(path) || Directory.Exists(path))
                list.Add(name);
        }

        return list;
    }

    private void AcceptPlace(string peer, byte[] payload)
    {
        var place = MessageCodec.DecodeJson<EdgeDragPlacePayload>(payload);
        if (place == null) return;
        TaskCompletionSource<EdgeDragPlacePayload?>? tcs;
        lock (_gate)
        {
            if (_phase != 2 || !string.Equals(_session, place.SessionId, StringComparison.Ordinal))
                return;
            if (!string.Equals(_awaitingPeer, peer, StringComparison.OrdinalIgnoreCase))
                return;
            tcs = _placeTcs;
            _placeTcs = null;
        }

        tcs?.TrySetResult(place);
    }

    private async Task SendQueryAsync(string peer, string session, string[] paths, TaskCompletionSource<EdgeDragPlacePayload?> tcs)
    {
        try
        {
            await Task.Delay(80).ConfigureAwait(false);
            await _connections.SendToAsync(peer, MessageType.EdgeDragQuery,
                MessageCodec.JsonBytes(new EdgeDragQueryPayload
                {
                    SessionId = session,
                    Names = TopLevelNames(paths),
                    SourceFolder = SourceFolderLabel(paths),
                    Items = BuildItemInfos(paths)
                })).ConfigureAwait(false);
            // 对端可能弹出替换/跳过，留给用户点选
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(2))).ConfigureAwait(false);
            if (finished != tcs.Task)
            {
                Report("穿边拖放超时");
                Clear();
                return;
            }

            var place = await tcs.Task.ConfigureAwait(false);
            if (place == null)
            {
                Clear();
                return;
            }

            var dir = place.Directory;
            if (string.IsNullOrWhiteSpace(dir) || place.Action == (int)EdgeDragConflictAction.Cancel)
            {
                Report(string.IsNullOrWhiteSpace(dir) ? "这里不能放下文件" : "已取消拖放");
                Clear();
                return;
            }

            var sending = paths;
            if (place.Action == (int)EdgeDragConflictAction.Skip && place.SkipNames is { Length: > 0 })
            {
                var skip = new HashSet<string>(place.SkipNames, NameComparer);
                sending = paths.Where(p => !skip.Contains(TopLevelName(p))).ToArray();
                if (sending.Length == 0)
                {
                    Report("已跳过同名文件");
                    Clear();
                    return;
                }
            }

            await _transfers.StartDirectTransferAsync(peer, dir, sending).ConfigureAwait(false);
        }
        catch
        {
            // 单次放下失败不打断连接
        }
        finally
        {
            Clear();
        }
    }

    private static string[] TopLevelNames(IReadOnlyList<string> paths)
    {
        var list = new List<string>();
        foreach (var raw in paths)
        {
            if (list.Count >= 500) break;
            var name = TopLevelName(raw);
            if (name.Length == 0) continue;
            list.Add(name);
        }

        return list.ToArray();
    }

    private static string SourceFolderLabel(IReadOnlyList<string> paths)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var trimmed = raw.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrWhiteSpace(parent)) continue;
                var name = Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch
            {
                // 下一项
            }
        }

        return "";
    }

    private static EdgeDragItemInfo[] BuildItemInfos(IReadOnlyList<string> paths)
    {
        var list = new List<EdgeDragItemInfo>();
        foreach (var raw in paths)
        {
            if (list.Count >= 500) break;
            var name = TopLevelName(raw);
            if (name.Length == 0) continue;
            var info = new EdgeDragItemInfo { Name = name };
            try
            {
                if (Directory.Exists(raw))
                {
                    info.IsDirectory = true;
                    info.LastWriteUtcTicks = Directory.GetLastWriteTimeUtc(raw).Ticks;
                }
                else if (File.Exists(raw))
                {
                    var fi = new FileInfo(raw);
                    info.Size = fi.Length;
                    info.LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks;
                }
            }
            catch
            {
                // 只带名称
            }

            list.Add(info);
        }

        return list.ToArray();
    }

    private static string TopLevelName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static StringComparer NameComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private async Task SendSwitchAsync(string peer, double entryX, double entryY)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.SwitchFocus,
                MessageCodec.JsonBytes(new SwitchFocusPayload
                {
                    TargetMachine = peer,
                    EntryX = entryX,
                    EntryY = entryY
                })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private async Task SendMoveAsync(string peer, double nx, double ny)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.MouseMove,
                MessageCodec.JsonBytes(new MouseMovePayload { Nx = nx, Ny = ny })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private void ApplyHover(byte[] payload)
    {
        var hover = MessageCodec.DecodeJson<EdgeDragHoverPayload>(payload);
        if (hover == null || string.IsNullOrEmpty(hover.SessionId)) return;

        EdgeDragCue? cue = null;
        lock (_gate)
        {
            if (!hover.Active)
            {
                if (!string.Equals(_shownCueSession, hover.SessionId, StringComparison.Ordinal))
                    return;
                _shownCueSession = "";
            }
            else
            {
                _shownCueSession = hover.SessionId;
                var names = hover.Names ?? [];
                cue = new EdgeDragCue
                {
                    SessionId = hover.SessionId,
                    Count = hover.Count > 0 ? hover.Count : names.Length,
                    Names = names
                };
            }
        }

        NotifyCue(cue);
    }

    private void EndCue()
    {
        string? peer;
        string session;
        lock (_gate)
        {
            peer = _cuePeer;
            session = _session;
            _cuePeer = null;
        }

        if (!string.IsNullOrEmpty(peer) && !IsSelf(peer) && session.Length > 0)
            _ = SendHoverAsync(peer, session, false, 0, []);
    }

    private async Task SendHoverAsync(string peer, string session, bool active, int count, string[] names)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.EdgeDragHover,
                MessageCodec.JsonBytes(new EdgeDragHoverPayload
                {
                    SessionId = session,
                    Active = active,
                    Count = count,
                    Names = names
                })).ConfigureAwait(false);
        }
        catch
        {
            // 提示丢失不影响随后的放下
        }
    }

    private static string[] DisplayNames(IReadOnlyList<string> paths)
    {
        var list = new List<string>();
        foreach (var raw in paths)
        {
            if (list.Count >= 8) break;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = trimmed;
            if (name.Length > 64) name = name[..64];
            list.Add(name);
        }

        return list.ToArray();
    }

    private static bool SamePeer(string? a, string? b)
        => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private void Report(string message) => UiMarshal.Post(() => StatusMessage?.Invoke(message));

    public event Action<bool>? ActiveChanged;

    private void NotifyActive(bool active) => UiMarshal.Post(() => ActiveChanged?.Invoke(active));

    private void NotifyCue(EdgeDragCue? cue) => UiMarshal.Post(() => CueChanged?.Invoke(cue));

    private void AnnounceOwner(string owner)
    {
        _ = _connections.BroadcastAsync(MessageType.CursorOwner,
            MessageCodec.JsonBytes(new CursorOwnerPayload { OwnerMachine = owner }));
    }

    private async Task<bool> WaitUntilCenteredAsync(IEdgeDragPointer pointer, int generation)
    {
        for (var i = 0; i < 8; i++)
        {
            if (generation != Volatile.Read(ref _followGeneration)) return false;
            if (!pointer.TryRead(out var x, out var y, out var down) || !down) return false;
            pointer.ScreenSize(out var sw, out var sh);
            if (Math.Abs(x - sw / 2) < sw / 6 && Math.Abs(y - sh / 2) < sh / 6)
                return true;
            pointer.WarpToCenter();
            await Task.Delay(16).ConfigureAwait(false);
        }

        return pointer.TryRead(out _, out _, out var still) && still;
    }

    private static int LocalExit(int x, int y, int sw, int sh)
    {
        if (x <= 2) return -1;
        if (x >= sw - 3) return 1;
        if (y <= 2) return -2;
        if (y >= sh - 3) return 2;
        return 0;
    }

    private void TryHop(int exit, ref string current, EdgeMouseVirtualCursor cursor)
    {
        var from = string.IsNullOrEmpty(current) ? LocalPeerIdentity.MachineName : current;
        var next = LayoutEdge.Neighbor(_settings, from, exit);
        if (string.IsNullOrWhiteSpace(next) || IsSelf(next))
        {
            if (!string.IsNullOrEmpty(current))
                AnnounceOwner(LocalPeerIdentity.MachineName);
            SetHover(null);
            current = "";
            cursor.ClampInside();
            return;
        }

        if (!CanAccept(next))
        {
            cursor.ClampInside();
            return;
        }

        var (ex, ey) = EntryPoint(exit);
        cursor.BeginRemote(ex, ey, exit);
        current = next;
        SetHover(next);
        AnnounceOwner(next);
        _ = SendSwitchAsync(next, ex, ey);
    }

    private static (double X, double Y) EntryPoint(int exitSide) => exitSide switch
    {
        -1 => (0.999, 0.5),
        1 => (0.001, 0.5),
        -2 => (0.5, 0.999),
        2 => (0.5, 0.001),
        _ => (0.5, 0.5)
    };

    private static bool IsSelf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        var env = Environment.MachineName;
        return !LocalPeerIdentity.IsBogusMachineName(env)
               && string.Equals(name, env, StringComparison.OrdinalIgnoreCase);
    }

    private void Clear()
    {
        EndCue();
        bool ended;
        bool hideCue;
        lock (_gate)
        {
            ended = _phase != 0;
            hideCue = _shownCueSession.Length > 0;
            _phase = 0;
            _session = "";
            _paths = [];
            _names = [];
            _hoverPeer = null;
            _cuePeer = null;
            _shownCueSession = "";
            _awaitingPeer = null;
            _placeTcs?.TrySetResult(null);
            _placeTcs = null;
        }

        if (hideCue)
            NotifyCue(null);
        if (ended)
            NotifyActive(false);
    }
}

/// <summary>目标机器上跟着手势走的拖拽提示。</summary>
public sealed class EdgeDragCue
{
    public string SessionId { get; init; } = "";
    public int Count { get; init; }
    public IReadOnlyList<string> Names { get; init; } = [];
}

/// <summary>拖拽期间读取本机指针。Mac / Linux 在没有全局钩子时用它把光标继续带到下一台。</summary>
public interface IEdgeDragPointer
{
    bool TryRead(out int x, out int y, out bool leftDown);
    void WarpToCenter();
    void ScreenSize(out int width, out int height);
    void DismissLocalDrag();
}
