namespace FlyScreen.Helpers;

/// <summary>
/// 跨平台 UI 调度入口。由宿主 App 在启动时注入（WPF/Avalonia 等）。
/// Core 内模型属性通知通过此入口回投 UI 线程，避免依赖具体 UI 框架。
/// </summary>
public static class UiMarshal
{
    private static Action<Action>? _post;
    private static Action<Action>? _send;

    public static void Configure(Action<Action> post, Action<Action>? send = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _send = send;
    }

    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_post != null) _post(action);
        else action();
    }

    public static void Send(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_send != null) _send(action);
        else if (_post != null) _post(action);
        else action();
    }
}
