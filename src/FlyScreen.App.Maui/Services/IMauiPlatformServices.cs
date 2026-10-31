using FlyScreen.Services.Clipboard;

namespace FlyScreen.AppMaui.Services;

/// <summary>平台能力：键鼠 / 剪贴板监听 / 托盘 / 自启。由 Platforms/* 实现。</summary>
public interface IMauiPlatformServices
{
    IClipboardApplySink ClipboardSink { get; }
    bool SupportsDesktopInput { get; }
    bool SupportsTray { get; }
    bool SupportsAutoStart { get; }
    bool IsFocusOnLocalMachine { get; }
    /// <summary>是否接受对端键鼠注入（桌面：须先 SwitchFocus 进入被控）。</summary>
    bool AcceptsRemoteInput { get; }

    /// <summary>
    /// 手机触控板页前台：屏蔽对端「穿透」键鼠（避免与触控板抢控制）。
    /// 电脑主动穿入或 App 退后台时会自动解除。不影响本机外接物理鼠标。
    /// </summary>
    void SetRemotePointerSuppressed(bool suppressed);

    /// <summary>Windows 桌面：把穿边拖文件接到键鼠钩子。其它端忽略。</summary>
    void AttachEdgeFileDrag(EdgeFileDragService drag) { }

    void StartDesktopInput();
    void StopDesktopInput();
    void SetEasyMouse(bool enabled);

    void InjectMouseMoveAbsolute(double nx, double ny);
    void InjectMouseMoveRelative(int dx, int dy);
    void InjectMouseButton(int button, bool down);
    void InjectMouseWheel(int delta, bool horizontal);
    void InjectKey(ushort vk, bool down, bool extended);
    /// <summary>电脑侧组字确认后的文本插入（中文词组等）。</summary>
    void InjectText(string text);
    void BeginReceivingRemoteControl(double entryX, double entryY);
    void OnCursorOwnerChanged(string ownerMachine);
    /// <summary>布局仅剩本机或被踢出集群时恢复本机光标。</summary>
    void RestoreCursorWhenLeavingCluster();

    void StartClipboardMonitor();
    void StopClipboardMonitor();
    void ArmRemoteClipboardForPaste();

    bool GetAutoStartEnabled();
    void SetAutoStartEnabled(bool enabled);

    /// <summary>PC：仅确保托盘图标（对齐 Win InitTray，不隐藏主窗）。</summary>
    void EnsureTray(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null);

    /// <summary>PC：隐藏到托盘；Android：退到后台（无托盘）。</summary>
    void HideToBackground(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null);
    void DisposeTray();

    /// <summary>Win 扩展屏：立刻准备本机第二输出（虚拟驱动/已有副屏）。
    /// <paramref name="layoutSide"/>：对端在布局中相对本机的方位（-1左/+1右/-2上/+2下）。</summary>
    Task<(bool Ok, string? InfoOrError)> EnsureScreenExtendHostDisplayAsync(
        FlyScreen.Services.ScreenExtend.LayoutEdgeSide layoutSide =
            FlyScreen.Services.ScreenExtend.LayoutEdgeSide.Unknown);

    /// <summary>
    /// 手机：弹出 MediaProjection 授权并启动采集前台服务（须在 StartHost 前）。
    /// 桌面：空操作成功（采集由 Win 控制器在 HostSessionReady 后启动）。
    /// </summary>
    Task<(bool Ok, string? Error)> PrepareScreenExtendCaptureAsync(CancellationToken ct = default);

    /// <summary>Win 扩展屏：释放本机第二输出；手机：停止 MediaProjection 采集。</summary>
    Task ReleaseScreenExtendHostDisplayAsync();

    /// <summary>Win 扩展屏：设置采集用的投影方式。</summary>
    void SetScreenExtendProjectionMode(FlyScreen.Services.ScreenExtend.DisplayProjectionMode mode);

    /// <summary>Win 扩展屏：设置副屏分辨率/缩放选择。</summary>
    void SetScreenExtendDisplayChoice(FlyScreen.Services.ScreenExtend.ProjectionDisplayChoice? choice);

    event Action<string?>? ControlTargetChanged;
}

/// <summary>非桌面 / 未接线平台的空实现。</summary>
public sealed class NullMauiPlatformServices : IMauiPlatformServices
{
    public static NullMauiPlatformServices Instance { get; } = new();

    public IClipboardApplySink ClipboardSink { get; } = NullClipboardApplySink.Instance;
    public bool SupportsDesktopInput => false;
    public bool SupportsTray => false;
    public bool SupportsAutoStart => false;
    public bool IsFocusOnLocalMachine => true;
    public bool AcceptsRemoteInput => false;

    public void SetRemotePointerSuppressed(bool suppressed) { }
    public void StartDesktopInput() { }
    public void StopDesktopInput() { }
    public void SetEasyMouse(bool enabled) { }
    public void InjectMouseMoveAbsolute(double nx, double ny) { }
    public void InjectMouseMoveRelative(int dx, int dy) { }
    public void InjectMouseButton(int button, bool down) { }
    public void InjectMouseWheel(int delta, bool horizontal) { }
    public void InjectKey(ushort vk, bool down, bool extended) { }
    public void InjectText(string text) { }
    public void BeginReceivingRemoteControl(double entryX, double entryY) { }
    public void OnCursorOwnerChanged(string ownerMachine) { }
    public void RestoreCursorWhenLeavingCluster() { }
    public void StartClipboardMonitor() { }
    public void StopClipboardMonitor() { }
    public void ArmRemoteClipboardForPaste() { }
    public bool GetAutoStartEnabled() => false;
    public void SetAutoStartEnabled(bool enabled) { }
    public void EnsureTray(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null) { }
    public void HideToBackground(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null) { }
    public void DisposeTray() { }

    public Task<(bool Ok, string? InfoOrError)> EnsureScreenExtendHostDisplayAsync(
        FlyScreen.Services.ScreenExtend.LayoutEdgeSide layoutSide =
            FlyScreen.Services.ScreenExtend.LayoutEdgeSide.Unknown)
        => Task.FromResult<(bool, string?)>((false, "当前平台不支持扩展屏主机"));

    public Task<(bool Ok, string? Error)> PrepareScreenExtendCaptureAsync(CancellationToken ct = default)
        => Task.FromResult<(bool, string?)>((true, null));

    public Task ReleaseScreenExtendHostDisplayAsync() => Task.CompletedTask;

    public void SetScreenExtendProjectionMode(FlyScreen.Services.ScreenExtend.DisplayProjectionMode mode) { }

    public void SetScreenExtendDisplayChoice(FlyScreen.Services.ScreenExtend.ProjectionDisplayChoice? choice) { }

    public event Action<string?>? ControlTargetChanged
    {
        add { }
        remove { }
    }
}
