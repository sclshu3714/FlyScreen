namespace FlyScreen.ScreenExtend;

/// <summary>
/// 为主机扩展会话准备第二输出：优先 MTT VDD；否则使用已有非主显示器。
/// </summary>
public sealed class VirtualDisplaySession : IAsyncDisposable
{
    public const string InstallHint =
        "请安装 Virtual Display Driver（winget install VirtualDrivers.Virtual-Display-Driver），或连接物理副屏后重试。";

    private bool _createdVirtual;
    private bool _manageVirtualCount;
    private MonitorRect? _target;

    public MonitorRect? Target => _target;
    /// <summary>本会话通过 MTT 管道创建/占用了虚拟屏（停止时需收拢）。</summary>
    public bool UsedVirtualDriver => _createdVirtual || _manageVirtualCount;

    public void RefreshTarget(MonitorRect rect) => _target = rect;

    public async Task<(bool Ok, string? Error)> EnsureAsync(
        CancellationToken ct = default,
        Services.ScreenExtend.LayoutEdgeSide layoutSide = Services.ScreenExtend.LayoutEdgeSide.Unknown,
        int? secondaryWidth = null,
        int? secondaryHeight = null,
        int? secondaryRefreshHz = null)
    {
        // 已有副屏且无新方位/分辨率要求：复用
        if (_target != null
            && layoutSide == Services.ScreenExtend.LayoutEdgeSide.Unknown
            && secondaryWidth == null)
            return (true, null);

        var existing = DisplayMonitorEnum.FindBestExtendTarget();

        var pipeOk = await MttVddPipeClient.IsPipePresentAsync(ct).ConfigureAwait(false)
                     || await MttVddPipeClient.PingAsync(ct).ConfigureAwait(false);
        if (existing == null && !pipeOk)
        {
            return (false,
                "未检测到第二显示器，且本机未运行虚拟显示驱动（MTTVirtualDisplayPipe）。" + InstallHint);
        }

        // 用过管道：停止时收拢拓扑，并在驱动重载后把控制台拉回来
        if (pipeOk)
            _manageVirtualCount = true;

        if (!await MttVddEnvironment.EnsureVirtualMonitorVisibleAsync(
                    ct: ct,
                    layoutSide: layoutSide,
                    secondaryWidth: secondaryWidth,
                    secondaryHeight: secondaryHeight,
                    secondaryRefreshHz: secondaryRefreshHz)
                .ConfigureAwait(false))
        {
            // 检查刚通过、点开始却失败：多为 SETDISPLAYCOUNT 后管道瞬断。拉回控制台再试一次，勿直接逼用户重装。
            try
            {
                await MttVddInstaller.TryEnsureConsoleAsync(ct, waitAttempts: 12, delayMs: 600)
                    .ConfigureAwait(false);
            }
            catch { /* ignore */ }

            if (!await MttVddEnvironment.EnsureVirtualMonitorVisibleAsync(
                        ct: ct,
                        layoutSide: layoutSide,
                        secondaryWidth: secondaryWidth,
                        secondaryHeight: secondaryHeight,
                        secondaryRefreshHz: secondaryRefreshHz)
                    .ConfigureAwait(false))
            {
                return (false,
                    "已连接虚拟显示驱动，但未能在扩展拓扑中出现副屏（请确认 VDDControl 显示器数量≥1，且「设置→系统→显示」为扩展）。"
                    + InstallHint);
            }
        }

        if (existing == null)
            _createdVirtual = true;

        for (var i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            var m = DisplayMonitorEnum.FindBestExtendTarget();
            if (m != null)
            {
                _target = m;
                return (true, null);
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        return (false, "已请求虚拟显示器，但系统仍未枚举到扩展屏（可打开「设置 → 系统 → 显示」检查）。" + InstallHint);
    }

    public async ValueTask DisposeAsync()
    {
        var created = _createdVirtual;
        var managed = _manageVirtualCount;
        _createdVirtual = false;
        _manageVirtualCount = false;
        _target = null;
        if (!created && !managed)
            return;

        if (created)
        {
            // 只撤掉本会话新建的那块。SETDISPLAYCOUNT 会重载驱动，控制台经常因此退出。
            MttVddEnvironment.TrySetXmlMonitorCount(0);
            try
            {
                await MttVddPipeClient.SetDisplayCountAsync(0).ConfigureAwait(false);
                for (var i = 0; i < 15; i++)
                {
                    if (DisplayMonitorEnum.FindBestExtendTarget() == null)
                        break;
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            catch { /* ignore */ }
        }

        // 管道由 VDDControl 提供。重载后必须等到管道回来，否则下次打开投影又提示「自动修复」。
        // 此处只拉控制台，不新建副屏、不跑安装包。
        try
        {
            await Task.Delay(500).ConfigureAwait(false);
            await MttVddInstaller.TryEnsureConsoleAsync(waitAttempts: 14, delayMs: 700)
                .ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }
}
