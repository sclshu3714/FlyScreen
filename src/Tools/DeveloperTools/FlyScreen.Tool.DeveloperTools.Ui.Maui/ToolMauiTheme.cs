namespace FlyScreen.Tool.DeveloperTools.Ui.Maui;

internal static class ToolMauiTheme
{
    public static bool IsDark
        => Application.Current?.UserAppTheme == AppTheme.Dark
           || Application.Current?.RequestedTheme == AppTheme.Dark;
}
