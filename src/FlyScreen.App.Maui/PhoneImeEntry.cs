namespace FlyScreen.AppMaui;

/// <summary>
/// 触控板键盘载体。Android 上由 <c>PhoneRemoteEditText</c> 把系统输入法的
/// 上屏、删除、回车和剪贴板命令转成 <see cref="RemoteText"/> / <see cref="RemoteKey"/>。
/// </summary>
public sealed class PhoneImeEntry : Entry
{
    /// <summary>已提交文本（不含组字中的拼音）。</summary>
    public event Action<string>? RemoteText;

    /// <summary>一次按键：按下并抬起。shift/ctrl 为修饰，由接收端组合成组合键。</summary>
    public event Action<ushort, bool, bool, bool>? RemoteKey;

    internal void EmitText(string text)
    {
        if (text.Length > 0)
            RemoteText?.Invoke(text);
    }

    internal void EmitKey(ushort virtualKey, bool extended, bool shift, bool ctrl)
        => RemoteKey?.Invoke(virtualKey, extended, shift, ctrl);
}
