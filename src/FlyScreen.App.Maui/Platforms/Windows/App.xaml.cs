using Microsoft.UI.Xaml;

namespace FlyScreen.AppMaui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => global::FlyScreen.AppMaui.MauiProgram.CreateMauiApp();
}
