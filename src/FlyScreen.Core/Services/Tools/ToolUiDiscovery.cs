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

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name == null || !name.StartsWith("FlyScreen.Tool.", StringComparison.OrdinalIgnoreCase))
                continue;

            Type[] types;
            try { types = asm.GetExportedTypes(); }
            catch { continue; }

            foreach (var type in types)
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
