#if ANDROID
namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 跨屏键盘 → 当前程序编辑器（Monaco）。有 sink 时勿再走透明 IME 代理。
/// </summary>
public static class RemoteTextInputHub
{
    private static IRemoteTextInputSink? _sink;

    public static void Register(IRemoteTextInputSink sink) => _sink = sink;

    public static void Unregister(IRemoteTextInputSink sink)
    {
        if (ReferenceEquals(_sink, sink))
            _sink = null;
    }

    public static bool HasSink => _sink != null;

    /// <summary>跨屏按键交给编辑器；true 表示已消费。</summary>
    public static bool TryHandleRemoteKey(ushort virtualKey, bool down, bool shift, bool caps, bool ctrl)
        => _sink?.TryHandleRemoteKey(virtualKey, down, shift, caps, ctrl) == true;

    /// <summary>电脑侧确认的文本写入当前编辑器。</summary>
    public static bool TryAcceptText(string text)
        => _sink?.TryAcceptRemoteText(text) == true;
}

public interface IRemoteTextInputSink
{
    bool TryHandleRemoteKey(ushort virtualKey, bool down, bool shift, bool caps, bool ctrl);
    bool TryAcceptRemoteText(string text);
}
#endif
