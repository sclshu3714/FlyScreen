namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 平台剪贴板写入（收到对端内容后应用到本机）。
/// Win：WPF Clipboard；Mac/Linux 桌面：系统 pasteboard/selection；CLI：可写 stdout/忽略。
/// </summary>
public interface IClipboardApplySink
{
    void ApplyText(string text);
    void ApplyInline(ClipboardInlineData data);
}
