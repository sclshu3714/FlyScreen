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

    /// <summary>平台专属按钮（如「打开辅助功能设置」）；无则空。</summary>
    IReadOnlyList<(string Label, Action Invoke)> PlatformActions { get; }

    bool SupportsTray { get; }
    bool SupportsAutoStart { get; }

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

    /// <summary>投影：Mac/Linux 本期不发起推流（可停止本地接收会话）；发起请用 Windows。</summary>
    Task<(bool Ok, string? Message)> StartProjectionAsync(ProjectionTarget? target, DisplayProjectionMode mode);

    Task StopProjectionAsync();

    bool GetAutoStartEnabled();
    void SetAutoStartEnabled(bool enabled);

    ObservableCollection<TransferItem> TransferItems { get; }
}
