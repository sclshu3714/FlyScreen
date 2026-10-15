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

    void StartDesktopInput();
    void StopDesktopInput();
    void SetEasyMouse(bool enabled);

    void InjectMouseMoveAbsolute(double nx, double ny);
    void InjectMouseMoveRelative(int dx, int dy);
    void InjectMouseButton(int button, bool down);
    void InjectMouseWheel(int delta, bool horizontal);
    void InjectKey(ushort vk, bool down, bool extended);
    void BeginReceivingRemoteControl(double entryX, double entryY);
    void OnCursorOwnerChanged(string ownerMachine);

    void StartClipboardMonitor();
    void StopClipboardMonitor();
    void ArmRemoteClipboardForPaste();

    bool GetAutoStartEnabled();
    void SetAutoStartEnabled(bool enabled);

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

    public void StartDesktopInput() { }
    public void StopDesktopInput() { }
    public void SetEasyMouse(bool enabled) { }
    public void InjectMouseMoveAbsolute(double nx, double ny) { }
    public void InjectMouseMoveRelative(int dx, int dy) { }
    public void InjectMouseButton(int button, bool down) { }
    public void InjectMouseWheel(int delta, bool horizontal) { }
    public void InjectKey(ushort vk, bool down, bool extended) { }
    public void BeginReceivingRemoteControl(double entryX, double entryY) { }
    public void OnCursorOwnerChanged(string ownerMachine) { }
    public void StartClipboardMonitor() { }
    public void StopClipboardMonitor() { }
    public void ArmRemoteClipboardForPaste() { }
    public bool GetAutoStartEnabled() => false;
    public void SetAutoStartEnabled(bool enabled) { }
    public void HideToBackground(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null) { }
    public void DisposeTray() { }

    public event Action<string?>? ControlTargetChanged
    {
        add { }
        remove { }
    }
}
