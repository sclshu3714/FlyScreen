namespace FlyScreen.Services.Clipboard;

/// <summary>将共享传输层收到的内容写入 Windows 剪贴板。</summary>
internal sealed class WindowsClipboardApplySink : IClipboardApplySink
{
    public void ApplyText(string text) => ClipboardBridge.SetTextSuppressing(text);

    public void ApplyInline(ClipboardInlineData data) => ClipboardBridge.ApplyInlineSuppressing(data);
}
