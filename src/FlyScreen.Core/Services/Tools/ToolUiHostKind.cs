namespace FlyScreen.Services.Tools;

/// <summary>按已加载框架判定宿主 UI 种类，用于筛选工具卫星程序集 / 贡献者。</summary>
public static class ToolUiHostKind
{
    /// <summary>如 <c>.Ui.Win</c> / <c>.Ui.Maui</c> / <c>.Ui.Desktop</c>。</summary>
    public static string PreferredAssemblySuffix
    {
        get
        {
            if (HasLoadedAssembly("Microsoft.Maui.Controls"))
                return ".Ui.Maui";
            if (HasLoadedAssembly("PresentationFramework"))
                return ".Ui.Win";
            if (HasLoadedAssembly("Avalonia") || HasLoadedAssembly("Avalonia.Controls"))
                return ".Ui.Desktop";

            if (OperatingSystem.IsAndroid() || OperatingSystem.IsMacCatalyst())
                return ".Ui.Maui";
            if (OperatingSystem.IsWindows())
                return ".Ui.Win";
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                return ".Ui.Desktop";
            return ".Ui.Win";
        }
    }

    /// <summary>程序集名结尾标记，如 <c>Ui.Win</c>。</summary>
    public static string PreferredAssemblyMarker
        => PreferredAssemblySuffix.TrimStart('.');

    public static bool IsPreferredUiAssembly(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName)) return false;
        if (!assemblyName.Contains(".Ui.", StringComparison.OrdinalIgnoreCase))
            return false;
        return assemblyName.EndsWith(PreferredAssemblyMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLoadedAssembly(string name)
        => AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
}
