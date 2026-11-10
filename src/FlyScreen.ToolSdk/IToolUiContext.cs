namespace FlyScreen.ToolSdk;

/// <summary>宿主注入的平台 UI 上下文（窗口/导航/状态栏），工具 UI 不引用具体 MainWindow。</summary>
public interface IToolUiContext
{
    /// <summary>WPF/Avalonia Window，或 Maui Page 等。</summary>
    object? Owner { get; }

    /// <summary>写宿主状态栏（可选）。</summary>
    Action<string>? ReportStatus { get; }

    /// <summary>Maui：返回 <c>INavigation</c>；其它端可为 null。</summary>
    Func<object?>? GetNavigation { get; }
}

public sealed class ToolUiContext : IToolUiContext
{
    public object? Owner { get; init; }
    public Action<string>? ReportStatus { get; init; }
    public Func<object?>? GetNavigation { get; init; }
}
