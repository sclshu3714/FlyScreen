using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.Services;

/// <summary>
/// 阻止屏保/空闲锁屏：设置开启且存在存活对端时，对本机施加平台空闲抑制。
/// BehaviorSync 会把开关同步到集群，各端各自抑制，避免切回时已黑屏/锁屏。
/// </summary>
public sealed class ScreenSaverBlockService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly Action<bool> _setInhibited;
    private readonly object _gate = new();
    private bool _active;
    private bool _disposed;

    public ScreenSaverBlockService(
        AppSettings settings,
        ConnectionManager connections,
        Action<bool> setInhibited)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _setInhibited = setInhibited ?? throw new ArgumentNullException(nameof(setInhibited));
        _connections.PeerLinkChanged += OnPeerLinkChanged;
    }

    public bool IsActive
    {
        get { lock (_gate) return _active; }
    }

    public void Refresh()
    {
        if (_disposed) return;
        var want = _settings.BlockScreenSaver && HasLivePeer();
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            changed = want != _active;
            _active = want;
        }

        if (!changed) return;
        try { _setInhibited(want); }
        catch { /* ignore platform failures */ }
    }

    private bool HasLivePeer()
    {
        try
        {
            return _connections.ListLivePeerNames().Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnPeerLinkChanged(string name, bool outbound, bool inbound)
    {
        _ = name;
        _ = outbound;
        _ = inbound;
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _connections.PeerLinkChanged -= OnPeerLinkChanged; } catch { /* ignore */ }
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
        }

        try { _setInhibited(false); } catch { /* ignore */ }
    }
}
