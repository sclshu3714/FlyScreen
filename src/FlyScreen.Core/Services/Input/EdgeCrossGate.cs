namespace FlyScreen.Services.Input;

/// <summary>
/// 穿边门控：把「已贴边/已出界」的尝试，按 <see cref="EdgeCrossMode"/> 决定是否真正切换。
/// 平台无关；Win / MAUI Win 等宿主在检测到边缘候选后调用 <see cref="TryAdmit"/>.
/// </summary>
public sealed class EdgeCrossGate
{
    /// <summary>无障碍：连续外推停留毫秒（与历史 EdgeDwellMs 对齐）。</summary>
    public const int BarrierFreeDwellMs = 140;

    /// <summary>双击：两次撞击最大间隔。</summary>
    public const int DoubleTapWindowMs = 1000;

    /// <summary>极速：判定「较远」——离开边后曾深入内侧的最小归一化深度。</summary>
    public const double SpeedBurstFarNorm = 0.12;

    /// <summary>极速：接近速度阈值（归一化屏幕/秒）。约 1 屏/秒，对应正常快速甩入。</summary>
    public const double SpeedBurstMinNormPerSec = 0.85;

    /// <summary>极速：贴边后仍沿用接近峰速的保持时间（物理光标贴边后位移为 0）。</summary>
    public const int SpeedBurstPeakHoldMs = 240;

    private EdgeCrossMode _mode = EdgeCrossMode.BarrierFree;

    private int _pendingSide;
    private long _pendingEnterTick;

    // 双击：离开边后再撞边算一次
    private int _tapSide;
    private int _tapCount;
    private long _tapLastTick;
    private bool _wasOnEdge;

    // 极速：采样位置估算速度 + 峰速保持；内侧深度（非「最后一帧位置」）
    private double _sampleNx = 0.5;
    private double _sampleNy = 0.5;
    private long _sampleTick;
    private double _velNx;
    private double _velNy;
    private double _burstVelNx;
    private double _burstVelNy;
    private long _burstVelTick;
    private double _inlandDepth;

    public EdgeCrossMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            Reset();
        }
    }

    public void Reset()
    {
        _pendingSide = 0;
        _pendingEnterTick = 0;
        _tapSide = 0;
        _tapCount = 0;
        _tapLastTick = 0;
        _wasOnEdge = false;
        _velNx = 0;
        _velNy = 0;
        _burstVelNx = 0;
        _burstVelNy = 0;
        _burstVelTick = 0;
        _inlandDepth = 0;
    }

    /// <summary>
    /// 每帧更新（任意鼠标移动）。用于极速模式速度估计与双击「离开边」检测。
    /// </summary>
    /// <param name="nx">归一化 X [0,1]（虚拟屏相对当前机）</param>
    /// <param name="ny">归一化 Y</param>
    /// <param name="onEdge">当前是否贴在某条可穿边上</param>
    public void Observe(double nx, double ny, bool onEdge, long nowTickMs)
    {
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);

        if (_sampleTick > 0)
        {
            var dt = (nowTickMs - _sampleTick) / 1000.0;
            if (dt > 0.001 && dt < 0.35)
            {
                _velNx = (nx - _sampleNx) / dt;
                _velNy = (ny - _sampleNy) / dt;
                UpdateBurstPeak(nowTickMs);
            }
        }

        if (!onEdge)
        {
            if (_wasOnEdge)
            {
                // 刚离开边：重新累计内侧深度，避免贴边失败后沿边蹭一下就再次满足「来自远处」
                _inlandDepth = InlandDepth(nx, ny);
            }
            else
            {
                _inlandDepth = Math.Max(_inlandDepth, InlandDepth(nx, ny));
            }

            _pendingSide = 0;
            if (_mode == EdgeCrossMode.DoubleTap && nowTickMs - _tapLastTick > DoubleTapWindowMs)
            {
                _tapCount = 0;
                _tapSide = 0;
            }
        }

        _sampleNx = nx;
        _sampleNy = ny;
        _sampleTick = nowTickMs;
        _wasOnEdge = onEdge;
    }

    /// <summary>
    /// 在已经判定「朝某侧外推贴边」时调用。返回 true 表示允许本次穿边。
    /// </summary>
    /// <param name="side">-1 左, +1 右, -2 上, +2 下</param>
    public bool TryAdmit(int side, long nowTickMs)
    {
        if (side == 0) return false;

        return _mode switch
        {
            EdgeCrossMode.DoubleTap => AdmitDoubleTap(side, nowTickMs),
            EdgeCrossMode.SpeedBurst => AdmitSpeedBurst(side, nowTickMs),
            _ => AdmitBarrierFree(side, nowTickMs)
        };
    }

    /// <summary>
    /// 远端虚拟光标出界时的穿边判定。
    /// 无障碍：VirtualCursor 的过冲已等价「继续外推」，此处直接放行（勿再套 140ms dwell，
    /// 否则失败路径 ClampInside 会清掉过冲，永远凑不满停留时间 → 无法回到本机）。
    /// </summary>
    public bool TryAdmitRemoteExit(int side, double nx, double ny, int dx, int dy, long nowTickMs)
    {
        var onEdge = side != 0;
        Observe(Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1), onEdge, nowTickMs);

        if (_mode == EdgeCrossMode.BarrierFree)
            return true;

        if (_mode == EdgeCrossMode.SpeedBurst)
        {
            // 用峰速门控；dx/dy 仅作极弱兜底（钉中心后单帧像素往往很小）
            if (AdmitSpeedBurst(side, nowTickMs))
                return true;

            var speedBoost = Math.Sqrt(dx * (double)dx + dy * (double)dy);
            if (speedBoost >= 12 && CameFromFar())
            {
                Reset();
                return true;
            }

            return false;
        }

        // 双击：仍走次数门控
        return TryAdmit(side, nowTickMs);
    }

    /// <summary>双击/极速拒绝时应夹回内侧；无障碍不应因门控失败而夹回。</summary>
    public bool ShouldClampVirtualCursorOnReject =>
        _mode is EdgeCrossMode.DoubleTap or EdgeCrossMode.SpeedBurst;

    /// <summary>无障碍进入对端时关闭入口粘滞，否则慢速沿入口边外推会被粘滞夹回、无法回本机。</summary>
    public bool UseEntrySticky =>
        _mode is EdgeCrossMode.DoubleTap or EdgeCrossMode.SpeedBurst;

    private bool AdmitBarrierFree(int side, long now)
    {
        // 无障碍：贴边外推即穿，不额外停留（140ms 会感觉「卡一下」）
        _ = side;
        _ = now;
        Reset();
        return true;
    }

    private bool AdmitDoubleTap(int side, long now)
    {
        // 上升沿：刚贴边（上一帧不在边或换边）由 Observe 的 _wasOnEdge 辅助；
        // 这里每次 TryAdmit 在「持续贴边外推」时会被多次调用，需只在「新撞击」计数。
        // 约定：同一侧 pending 首次进入计一次；离开边后再次进入再计。
        if (_pendingSide != side)
        {
            // 新撞击
            if (_tapSide == side && now - _tapLastTick <= DoubleTapWindowMs)
                _tapCount++;
            else
            {
                _tapSide = side;
                _tapCount = 1;
            }

            _tapLastTick = now;
            _pendingSide = side;
            _pendingEnterTick = now;

            if (_tapCount >= 2)
            {
                Reset();
                return true;
            }

            return false;
        }

        // 同侧持续按住：不重复计数
        if (now - _tapLastTick > DoubleTapWindowMs)
        {
            _tapCount = 0;
            _tapSide = 0;
            _pendingSide = 0;
        }

        return false;
    }

    private bool AdmitSpeedBurst(int side, long now)
    {
        if (!CameFromFar())
            return false;

        // 优先峰速（贴边后瞬时速度常为 0）
        var vx = BurstVelNx(now);
        var vy = BurstVelNy(now);
        var speed = side is -1 or 1 ? Math.Abs(vx) : Math.Abs(vy);
        var toward = side switch
        {
            -1 => vx < 0,
            1 => vx > 0,
            -2 => vy < 0,
            2 => vy > 0,
            _ => false
        };
        if (!toward || speed < SpeedBurstMinNormPerSec)
            return false;

        Reset();
        return true;
    }

    private bool CameFromFar() => _inlandDepth >= SpeedBurstFarNorm;

    private static double InlandDepth(double nx, double ny)
        => Math.Min(Math.Min(nx, 1.0 - nx), Math.Min(ny, 1.0 - ny));

    private void UpdateBurstPeak(long now)
    {
        if (Math.Abs(_velNx) >= Math.Abs(_burstVelNx))
        {
            _burstVelNx = _velNx;
            _burstVelTick = now;
        }
        else if (now - _burstVelTick > SpeedBurstPeakHoldMs)
        {
            _burstVelNx = _velNx;
        }

        if (Math.Abs(_velNy) >= Math.Abs(_burstVelNy))
        {
            _burstVelNy = _velNy;
            _burstVelTick = now;
        }
        else if (now - _burstVelTick > SpeedBurstPeakHoldMs)
        {
            _burstVelNy = _velNy;
        }
    }

    private double BurstVelNx(long now)
        => now - _burstVelTick <= SpeedBurstPeakHoldMs ? _burstVelNx : _velNx;

    private double BurstVelNy(long now)
        => now - _burstVelTick <= SpeedBurstPeakHoldMs ? _burstVelNy : _velNy;
}
