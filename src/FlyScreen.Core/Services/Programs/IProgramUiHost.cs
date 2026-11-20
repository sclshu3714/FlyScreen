namespace FlyScreen.Services.Programs;

/// <summary>
/// 宿主侧展示子程序 UI（Maui ContentPage / Avalonia Window）。
/// WPF 走进程外 powershell，不依赖本接口。
/// </summary>
public interface IProgramUiHost
{
    /// <param name="uiDocumentName">如 ProgramUiPage.xaml / MainWindow.axaml（相对程序目录）。</param>
    Task<bool?> ShowAsync(string uiDocumentName, CancellationToken cancellationToken = default);
}

/// <summary>进程内当前 UI 宿主；Maui / Avalonia 启动时注册。</summary>
public static class ProgramUiHost
{
    public static IProgramUiHost? Current { get; private set; }

    public static void Register(IProgramUiHost? host) => Current = host;
}
