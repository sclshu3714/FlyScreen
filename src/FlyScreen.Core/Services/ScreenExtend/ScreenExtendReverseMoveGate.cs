namespace FlyScreen.Services.ScreenExtend;

/// <summary>
/// 从机全屏窗刚激活时，系统常会合成一次落在客户区左上角 (≈0,0) 的指针事件；
/// 若立刻回控到主机，主机会把光标打到采集屏左上角，投影画面里就会「闪一下」。
/// 附着后短暂抑制，并丢掉首条角落合成事件。
/// </summary>
public sealed class ScreenExtendReverseMoveGate
{
    private const int SettleMs = 350;
    private const double CornerEps = 0.03;

    private long _attachTick;
    private bool _armed;
    private double _lastNx = -1;
    private double _lastNy = -1;

    public void OnCaptureAttached()
    {
        _attachTick = Environment.TickCount64;
        _armed = false;
        _lastNx = _lastNy = -1;
    }

    public void OnCaptureDetached()
    {
        _armed = false;
        _lastNx = _lastNy = -1;
    }

    /// <summary>是否应发送移动回控。</summary>
    public bool ShouldSendMove(double nx, double ny)
    {
        var now = Environment.TickCount64;
        if (!_armed)
        {
            if (_attachTick != 0 && now - _attachTick < SettleMs)
                return false;
            // 全屏 Show/Activate 常合成左上角事件
            if (nx <= CornerEps && ny <= CornerEps)
                return false;
            _armed = true;
        }

        if (_lastNx >= 0
            && Math.Abs(nx - _lastNx) < 1e-6
            && Math.Abs(ny - _lastNy) < 1e-6)
            return false;

        _lastNx = nx;
        _lastNy = ny;
        return true;
    }

    /// <summary>按键/滚轮：用户明确操作，附着稍稳后即放行并武装移动通道。</summary>
    public bool ShouldSendPointer(double nx, double ny)
    {
        var now = Environment.TickCount64;
        if (_attachTick != 0 && now - _attachTick < 80)
            return false;

        _armed = true;
        _lastNx = nx;
        _lastNy = ny;
        return true;
    }
}
