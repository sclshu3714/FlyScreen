#if ANDROID
namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 跨屏键盘 → 当前页文本接收器（如程序编辑器 Monaco）。
/// WebView 无法可靠走无障碍 ACTION_SET_TEXT，需页面自行插入。
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

    public static bool TryCommit(string text)
        => _sink?.TryCommitText(text) == true;

    public static bool TryDelete(int utf16Count)
        => _sink?.TryDelete(utf16Count) == true;

    public static bool TrySpecialKey(ushort virtualKey)
        => _sink?.TrySpecialKey(virtualKey) == true;

    public static bool HasSink => _sink != null;
}

public interface IRemoteTextInputSink
{
    bool TryCommitText(string text);
    bool TryDelete(int utf16Count);
    bool TrySpecialKey(ushort virtualKey);
}
#endif
