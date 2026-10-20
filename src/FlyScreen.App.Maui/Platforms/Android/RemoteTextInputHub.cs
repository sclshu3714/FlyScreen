#if ANDROID
namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 跨屏键盘 → 当前页「原生组字」接收器（程序编辑器）。
/// 中文必须在可见 EditText 上由系统 IME 组字；Monaco 只收确认后的文本。
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

    /// <summary>跨屏按键交给组字页；true 表示已消费，勿再走透明 IME 代理 / Vk 直写。</summary>
    public static bool TryHandleRemoteKey(ushort virtualKey, bool down, bool shift, bool caps, bool ctrl)
        => _sink?.TryHandleRemoteKey(virtualKey, down, shift, caps, ctrl) == true;

    public static void ClearComposition() => _sink?.ClearComposition();

    /// <summary>电脑侧确认的文本（中文词组 / 英文直出）写入当前编辑器。</summary>
    public static bool TryAcceptText(string text)
        => _sink?.TryAcceptRemoteText(text) == true;
}

public interface IRemoteTextInputSink
{
    bool TryHandleRemoteKey(ushort virtualKey, bool down, bool shift, bool caps, bool ctrl);
    void ClearComposition();
    bool TryAcceptRemoteText(string text);
}
#endif
