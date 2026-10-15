using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FlyScreen.DesktopUi;
using FlyScreen.Linux;

namespace FlyScreen.LinuxApp.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 用运行时拼好的 UI 字体覆盖资源（含 CJK 集合）
        try
        {
            Resources["UiFont"] = new FontFamily(LinuxFontSetup.UiFontFamily);
        }
        catch
        {
            Resources["UiFont"] = new FontFamily(LinuxFontSetup.InterFamilyKey);
        }

        var host = LinuxAppHost.EnsureCreated();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(new LinuxDesktopShellHost(host));
            desktop.ShutdownRequested += async (_, e) =>
            {
                e.Cancel = true;
                try { await LinuxAppHost.Instance.DisposeAsync().ConfigureAwait(true); }
                catch { /* ignore */ }
                desktop.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
