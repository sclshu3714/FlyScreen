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

    /// <summary>
    /// 将当前系统主题应用到工具弹窗（WPF <c>Window</c> 等）。
    /// 宿主实现：如 Win 端 <c>ThemeService.ApplyToChildWindow</c>。
    /// </summary>
    Action<object>? ApplyWindowTheme { get; }
}

public sealed class ToolUiContext : IToolUiContext
{
    public object? Owner { get; init; }
    public Action<string>? ReportStatus { get; init; }
    public Func<object?>? GetNavigation { get; init; }
    public Action<object>? ApplyWindowTheme { get; init; }
}
