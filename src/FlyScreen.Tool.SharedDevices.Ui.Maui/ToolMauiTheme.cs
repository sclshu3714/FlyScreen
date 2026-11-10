namespace FlyScreen.Tool.SharedDevices.Ui.Maui;

/// <summary>工具 UI 读宿主已注入的主题资源，不改主程序主题服务。</summary>
internal static class ToolMauiTheme
{
    public static bool IsDark
        => Application.Current?.UserAppTheme == AppTheme.Dark
           || Application.Current?.RequestedTheme == AppTheme.Dark;

    public static Color? ColorOf(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return null;
    }
}

