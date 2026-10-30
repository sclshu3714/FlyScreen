namespace FlyScreen.Services.ScreenExtend;

/// <summary>
/// 投影从机：默认隐藏本机光标；本机鼠标一动就显示，静止 <see cref="IdleSeconds"/> 秒后再隐藏。
/// UI 层订阅 <see cref="ShowLocalCursorChanged"/> 并设置窗口 Cursor。
/// </summary>
public sealed class SinkLocalCursorIdleHide : IDisposable
{
    public const double IdleSeconds = 5;

    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private bool _show;
    private bool _disposed;
    private long _lastMoveTicks;

    public event Action<bool>? ShowLocalCursorChanged;

    public bool ShowLocalCursor
    {
        get { lock (_gate) return _show; }
    }

    public SinkLocalCursorIdleHide()
    {
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        SetShow(false);
    }

    /// <summary>投影会话开始：立即隐藏本机光标。</summary>
    public void BeginSession()
    {
        lock (_gate)
            _lastMoveTicks = 0;
        SetShow(false);
        ArmTimer();
    }

    /// <summary>本机指针移动（从机窗口内）。</summary>
    public void NotifyLocalMouseMoved()
    {
        lock (_gate)
            _lastMoveTicks = Environment.TickCount64;
        SetShow(true);
        ArmTimer();
    }

    /// <summary>会话结束：恢复显示本机光标。</summary>
    public void EndSession()
    {
        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { /* ignore */ }
        SetShow(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EndSession();
        _timer.Dispose();
    }

    private void ArmTimer()
    {
        try { _timer.Change(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)); }
        catch { /* ignore */ }
    }

    private void OnTick(object? _)
    {
        if (_disposed) return;
        long last;
        lock (_gate) last = _lastMoveTicks;
        if (last == 0)
        {
            SetShow(false);
            return;
        }

        var idleMs = Environment.TickCount64 - last;
        if (idleMs >= (long)(IdleSeconds * 1000))
            SetShow(false);
    }

    private void SetShow(bool show)
    {
        if (_disposed && !show) return;
        lock (_gate)
        {
            if (_show == show) return;
            _show = show;
        }

        try { ShowLocalCursorChanged?.Invoke(show); } catch { /* ignore */ }
    }
}
