using FlyScreen.Helpers;
using FlyScreen.Models;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 拖文件穿边：源端拿着路径，光标按布局穿过多台机器，松开时落到当前那台的目录。
/// </summary>
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
    private string? _hoverPeer;
    private string? _awaitingPeer;
    private TaskCompletionSource<string?>? _placeTcs;
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
    }

    public event Action<string>? StatusMessage;

    public bool IsActive
    {
        get { lock (_gate) return _phase != 0; }
    }

    /// <summary>扩展屏占用的对端不接收穿边拖拽。</summary>
    public Func<string, bool>? RejectPeer { get; set; }

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
            _hoverPeer = null;
            _awaitingPeer = null;
        }

        NotifyActive(true);
        return true;
    }

    public void SetHover(string? peerOrNullIfLocal)
    {
        lock (_gate)
        {
            if (_phase != 1) return;
            _hoverPeer = string.IsNullOrWhiteSpace(peerOrNullIfLocal) ? null : peerOrNullIfLocal;
        }
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

        if (string.IsNullOrWhiteSpace(hover) || IsSelf(hover))
        {
            PlaceHere(paths);
            Clear();
            return;
        }

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        }
    }

    private void ReplyToQuery(string peer, byte[] payload)
    {
        var query = MessageCodec.DecodeJson<EdgeDragQueryPayload>(payload);
        if (query == null || string.IsNullOrEmpty(query.SessionId)) return;
        if (!_settings.TransferFile) return;
        string dir;
        try { dir = _resolveDropDirectory() ?? ""; }
        catch { dir = ""; }
        _ = _connections.SendToAsync(peer, MessageType.EdgeDragPlace,
            MessageCodec.JsonBytes(new EdgeDragPlacePayload
            {
                SessionId = query.SessionId,
                Directory = dir
            }));
    }

    private void AcceptPlace(string peer, byte[] payload)
    {
        var place = MessageCodec.DecodeJson<EdgeDragPlacePayload>(payload);
        if (place == null) return;
        TaskCompletionSource<string?>? tcs;
        lock (_gate)
        {
            if (_phase != 2 || !string.Equals(_session, place.SessionId, StringComparison.Ordinal))
                return;
            if (!string.Equals(_awaitingPeer, peer, StringComparison.OrdinalIgnoreCase))
                return;
            tcs = _placeTcs;
            _placeTcs = null;
        }

        tcs?.TrySetResult(place.Directory);
    }

    private async Task SendQueryAsync(string peer, string session, string[] paths, TaskCompletionSource<string?> tcs)
    {
        try
        {
            await Task.Delay(80).ConfigureAwait(false);
            await _connections.SendToAsync(peer, MessageType.EdgeDragQuery,
                MessageCodec.JsonBytes(new EdgeDragQueryPayload { SessionId = session })).ConfigureAwait(false);
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(4000)).ConfigureAwait(false);
            if (finished != tcs.Task)
            {
                Report("穿边拖放超时");
                Clear();
                return;
            }

            var dir = await tcs.Task.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(dir))
            {
                Clear();
                return;
            }

            await _transfers.StartDirectTransferAsync(peer, dir, paths).ConfigureAwait(false);
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

    private void PlaceHere(string[] paths)
    {
        string dir;
        try { dir = _resolveDropDirectory() ?? ""; }
        catch { dir = ""; }
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            Directory.CreateDirectory(dir);
            var n = CopyInto(paths, dir);
            if (n > 0)
                Report($"穿边拖放 {n} 项 → {dir}");
        }
        catch
        {
            Report("穿边拖放失败");
        }
    }

    private static int CopyInto(IReadOnlyList<string> paths, string destDir)
    {
        var destRoot = Path.GetFullPath(destDir);
        var count = 0;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string src;
            try { src = Path.GetFullPath(raw); }
            catch { continue; }

            if (Directory.Exists(src))
            {
                var name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) continue;
                var destFull = Path.GetFullPath(destRoot);
                var srcFull = src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (destFull.Equals(srcFull, StringComparison.OrdinalIgnoreCase)
                    || destFull.StartsWith(srcFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                CopyDirectory(src, UniquePath(Path.Combine(destRoot, name)));
                count++;
            }
            else if (File.Exists(src))
            {
                var target = UniquePath(Path.Combine(destRoot, Path.GetFileName(src)));
                File.Copy(src, target);
                count++;
            }
        }

        return count;
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var sub in Directory.EnumerateDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var file = Path.GetFileName(path);
        var ext = Path.GetExtension(file);
        var stem = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrEmpty(ext) && Directory.Exists(path))
        {
            ext = "";
            stem = file;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, stem + "_" + Guid.NewGuid().ToString("N")[..6] + ext);
    }

    private void Report(string message) => UiMarshal.Post(() => StatusMessage?.Invoke(message));

    public event Action<bool>? ActiveChanged;

    private void NotifyActive(bool active) => UiMarshal.Post(() => ActiveChanged?.Invoke(active));

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
        bool ended;
        lock (_gate)
        {
            ended = _phase != 0;
            _phase = 0;
            _session = "";
            _paths = [];
            _hoverPeer = null;
            _awaitingPeer = null;
            _placeTcs?.TrySetResult(null);
            _placeTcs = null;
        }

        if (ended)
            NotifyActive(false);
    }
}

/// <summary>拖拽期间读取本机指针。Mac / Linux 在没有全局钩子时用它把光标继续带到下一台。</summary>
public interface IEdgeDragPointer
{
    bool TryRead(out int x, out int y, out bool leftDown);
    void WarpToCenter();
    void ScreenSize(out int width, out int height);
    void DismissLocalDrag();
}
