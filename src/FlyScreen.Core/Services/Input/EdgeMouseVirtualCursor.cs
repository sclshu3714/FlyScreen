namespace FlyScreen.Services.Input;

/// <summary>
/// 跨机边缘鼠标的虚拟光标状态（平台无关）。
/// 进入对端后对「进入边」做粘滞：须先移入内侧，再持续外推一小段，才允许从该边切回，
/// 避免入口附近微抖把光标甩回本机。
/// </summary>
public sealed class EdgeMouseVirtualCursor
{
    /// <summary>进入边粘滞释放阈值（归一化）：须离开进入边这么远才允许从该边退出。</summary>
    public const double StickyReleaseNorm = 0.14;

    /// <summary>出界过冲（归一化）：顶到边后还需再往外推这么多才触发切换。</summary>
    public const double ExitOvershootNorm = 0.05;

    /// <summary>单帧位移相对屏宽/高的上限，超过视为钉中心失败，忽略本帧。</summary>
    public const double MaxFrameDeltaNorm = 0.22;

    public double Nx { get; private set; } = 0.5;
    public double Ny { get; private set; } = 0.5;

    /// <summary>进入对端时的边：-1 左, +1 右, -2 上, +2 下（与 InputRouter.peerSide 相反：entryEdge = -peerSide）。</summary>
    public int EntryEdge { get; private set; }

    public bool EntryStickyActive { get; private set; }

    /// <summary>
    /// 是否启用进入边粘滞。无障碍模式应关闭：否则慢速沿入口边外推会被 <see cref="ClampInside"/> 清掉过冲，无法回本机。
    /// </summary>
    public bool EntryStickyEnabled { get; set; } = true;

    /// <summary>
    /// 开始控制某台对端。
    /// </summary>
    /// <param name="entryX">入口归一化 X</param>
    /// <param name="entryY">入口归一化 Y</param>
    /// <param name="peerSide">对端相对本机方向（-1 左…）；进入边为 -peerSide</param>
    public void BeginRemote(double entryX, double entryY, int peerSide)
    {
        Nx = Math.Clamp(entryX, 0.02, 0.98);
        Ny = Math.Clamp(entryY, 0.02, 0.98);
        EntryEdge = peerSide == 0 ? 0 : -peerSide;
        EntryStickyActive = EntryStickyEnabled && EntryEdge != 0;
    }

    public void Set(double nx, double ny)
    {
        Nx = Math.Clamp(nx, 0, 1);
        Ny = Math.Clamp(ny, 0, 1);
        MaybeReleaseSticky();
    }

    /// <summary>
    /// 累加相对位移（像素 / 屏尺寸 → 归一化）。
    /// </summary>
    /// <returns>false 表示本帧位移异常（钉中心失败等），调用方应重新钉住物理光标且不做出界判断。</returns>
    public bool TryApplyPixelDelta(int dx, int dy, int screenW, int screenH)
    {
        var w = Math.Max(1, screenW);
        var h = Math.Max(1, screenH);
        var ndx = dx / (double)w;
        var ndy = dy / (double)h;
        if (Math.Abs(ndx) > MaxFrameDeltaNorm || Math.Abs(ndy) > MaxFrameDeltaNorm)
            return false;

        // 允许轻微过冲，便于检测「持续外推」；发送绝对坐标时再夹到 [0,1]
        const double lo = -ExitOvershootNorm - 0.02;
        const double hi = 1.0 + ExitOvershootNorm + 0.02;
        Nx = Math.Clamp(Nx + ndx, lo, hi);
        Ny = Math.Clamp(Ny + ndy, lo, hi);
        MaybeReleaseSticky();
        return true;
    }

    /// <summary>
    /// 是否应从当前虚拟位置朝 (dx,dy) 方向出界切换。
    /// </summary>
    /// <param name="allowVertical">双行布局时允许上下边</param>
    /// <param name="dirH">出界水平方向 -1/0/+1</param>
    /// <param name="dirV">出界垂直方向 -2/0/+2（与 peerSide 约定一致）</param>
    public bool TryGetExitDirection(int dx, int dy, bool allowVertical, out int dirH, out int dirV)
    {
        dirH = 0;
        dirV = 0;

        if (dx < 0 && Nx <= -ExitOvershootNorm)
            dirH = -1;
        else if (dx > 0 && Nx >= 1.0 + ExitOvershootNorm)
            dirH = 1;

        if (allowVertical)
        {
            if (dy < 0 && Ny <= -ExitOvershootNorm)
                dirV = -2;
            else if (dy > 0 && Ny >= 1.0 + ExitOvershootNorm)
                dirV = 2;
        }

        if (dirH == 0 && dirV == 0)
            return false;

        // 水平优先
        if (dirH != 0)
        {
            if (IsBlockedByEntrySticky(dirH))
            {
                dirH = 0;
                ClampInside();
                return false;
            }

            dirV = 0;
            return true;
        }

        if (IsBlockedByEntrySticky(dirV))
        {
            dirV = 0;
            ClampInside();
            return false;
        }

        return true;
    }

    public void ClampInside()
    {
        Nx = Math.Clamp(Nx, 0, 1);
        Ny = Math.Clamp(Ny, 0, 1);
    }

    public (double Nx, double Ny) Clamped01()
        => (Math.Clamp(Nx, 0, 1), Math.Clamp(Ny, 0, 1));

    private void MaybeReleaseSticky()
    {
        if (!EntryStickyActive || EntryEdge == 0) return;

        var released = EntryEdge switch
        {
            -1 => Nx >= StickyReleaseNorm,
            1 => Nx <= 1.0 - StickyReleaseNorm,
            -2 => Ny >= StickyReleaseNorm,
            2 => Ny <= 1.0 - StickyReleaseNorm,
            _ => true
        };

        if (released)
            EntryStickyActive = false;
    }

    /// <summary>退出方向是否被进入边粘滞挡住（尚未移入内侧）。</summary>
    private bool IsBlockedByEntrySticky(int exitDir)
    {
        if (!EntryStickyActive || EntryEdge == 0) return false;
        // 只有「从进入边原路返回」才挡住；从对边离开不受影响
        return exitDir == EntryEdge;
    }
}
