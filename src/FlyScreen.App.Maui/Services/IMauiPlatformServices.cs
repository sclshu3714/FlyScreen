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

    public event Action<string?>? ControlTargetChanged
    {
        add { }
        remove { }
    }
}
