using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Services.Tools;

/// <summary>从已加载的 FlyScreen.Tool.* 程序集发现并注册 <see cref="IToolUiContributor"/>。</summary>
public static class ToolUiDiscovery
{
    public static void RegisterAll(IToolUiHost ui, IToolUiContext context)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(context);

        Loc.EnsureToolLocalizationsFromLoadedAssemblies();
        if (ui is Hosting.ToolUiHost concrete)
            concrete.ClearAll();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name == null || !name.StartsWith("FlyScreen.Tool.", StringComparison.OrdinalIgnoreCase))
                continue;
            // 只注册当前宿主 UI 栈的卫星贡献者（勿让 Ui.Maui 覆盖 WPF 工作区）
            if (name.Contains(".Ui.", StringComparison.OrdinalIgnoreCase)
                && !ToolUiHostKind.IsPreferredUiAssembly(name))
                continue;

            foreach (var type in ToolAssemblyScan.GetLoadableTypes(asm))
            {
                if (type.IsAbstract || !typeof(IToolUiContributor).IsAssignableFrom(type))
                    continue;
                try
                {
                    if (Activator.CreateInstance(type) is not IToolUiContributor contributor)
                        continue;
                    contributor.Register(ui, context);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"注册工具 UI 贡献者失败 {type.FullName}: {ex.Message}");
                }
            }
        }
    }
}
