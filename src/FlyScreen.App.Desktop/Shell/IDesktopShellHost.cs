using System.Collections.ObjectModel;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.DesktopUi.Shell;

/// <summary>
/// Mac / Linux 等 Avalonia 桌面端共用的宿主契约；UI 与 Win 对齐，业务由各平台 Host 提供。
/// </summary>
public interface IDesktopShellHost
{
    AppSettings Settings { get; }
    ConnectionManager Connections { get; }
    FileTransferService Transfers { get; }

    event Action<string>? StatusMessage;
    event Action<string>? LayoutSyncedFromPeer;
    event Action<string>? BehaviorSyncedFromPeer;
    event Action<string>? SecurityKeySyncedFromPeer;

    /// <summary>侧栏底部附加状态（如会话类型、辅助功能）。</summary>
    string PlatformStatusLine { get; }

    /// <summary>平台专属操作（如 Mac 辅助功能/屏幕录制）；无则空。Icon 为标题栏图标按钮文案。</summary>
    IReadOnlyList<(string Label, string Icon, Action Invoke)> PlatformActions { get; }

    bool SupportsTray { get; }
    bool SupportsAutoStart { get; }

    /// <summary>托盘「切换机器」：跳到布局中的本机/对端；无穿边路由时返回 false。</summary>
    bool TrySwitchToMachine(string machineName) => false;

    Task StartAsync();
    Task RestartNetworkingAsync();
    /// <summary>应用设置：默认保持连接；仅端口/网卡/密钥变化时整网重启。</summary>
    Task ApplyNetworkingAsync(
        int previousTcpPort,
        int previousUdpPort,
        string previousNicId,
        string previousLocalIp,
        string previousSecurityKey);
    Task BroadcastLayoutSyncAsync();
    Task BroadcastLayoutMergeAsync();
    Task BroadcastBehaviorSyncAsync();
    Task<(bool Ok, string Message)> SyncSecurityKeyAsync(CancellationToken ct = default);

    ScreenExtendService ScreenExtend { get; }

    /// <summary>本端能否作为投影主机推流。Mac 可以；Linux 仍只接收。</summary>
    bool CanStartProjection => false;

    /// <summary>发起前的环境说明。Ready 为 false 时开始按钮不可用。</summary>
    (bool Ready, string Message) DescribeProjectionEnvironment(DisplayProjectionMode mode)
        => (false, "");

    /// <summary>发起投影。不能发起的端返回失败说明，仍可停止本机正在接收的会话。</summary>
    Task<(bool Ok, string? Message)> StartProjectionAsync(
        ProjectionTarget? target, DisplayProjectionMode mode,
        bool audioSync = false, bool cropBlackBars = false, bool remoteControl = false);

    Task StopProjectionAsync();

    bool GetAutoStartEnabled();
    void SetAutoStartEnabled(bool enabled);

    ObservableCollection<TransferItem> TransferItems { get; }

    /// <summary>能在拖住文件时继续读本机指针，从而按布局穿到再下一台。</summary>
    bool CanFollowEdgeFileDrag => false;

    /// <summary>屏幕边缘收到文件拖拽。exitSide：-1 左、+1 右、-2 上、+2 下。</summary>
    void OfferEdgeFileDrag(IReadOnlyList<string> paths, int exitSide) { }

    /// <summary>穿边拖拽开始或结束，用来收起挡住屏幕边缘的接收条。</summary>
    event Action<bool>? EdgeFileDragActiveChanged { add { } remove { } }

    /// <summary>对端把文件拖到本机时，显示或收起跟手提示。</summary>
    event Action<EdgeDragCue?>? EdgeDragCueChanged { add { } remove { } }

    /// <summary>本机指针的屏幕像素坐标。拖拽提示跟着它走。</summary>
    bool TryGetEdgeDragPointer(out int x, out int y)
    {
        x = y = 0;
        return false;
    }

    /// <summary>松开后文件会落到的位置说明。</summary>
    string EdgeDragDropLabel => "桌面";
}
