using System.Collections.ObjectModel;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;

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

    /// <summary>侧栏底部附加状态（如会话类型、辅助功能）。</summary>
    string PlatformStatusLine { get; }

    /// <summary>平台专属按钮（如「打开辅助功能设置」）；无则空。</summary>
    IReadOnlyList<(string Label, Action Invoke)> PlatformActions { get; }

    bool SupportsTray { get; }
    bool SupportsAutoStart { get; }

    Task StartAsync();
    Task RestartNetworkingAsync();
    Task BroadcastLayoutSyncAsync();
    Task BroadcastLayoutMergeAsync();
    Task BroadcastBehaviorSyncAsync();

    bool GetAutoStartEnabled();
    void SetAutoStartEnabled(bool enabled);

    ObservableCollection<TransferItem> TransferItems { get; }
}
