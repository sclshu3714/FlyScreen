using FlyScreen.Services;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.AppMaui.Tools.Ui;

public static class ToolUiBootstrap
{
    public static void RegisterAll(Func<INavigation> navigation, Action<string>? reportStatus = null)
    {
        var tools = ToolService.Instance;
        var ctx = new ToolUiContext
        {
            Owner = null,
            // 与 Win 对齐：插件 ReportStatus → 日志页；此前 Maui 未接线导致播放日志全丢
            ReportStatus = reportStatus ?? (s => AppLog.RecordStatus(s)),
            GetNavigation = () => navigation()
        };
        tools.BindUi(ctx);
        ToolUiDiscovery.RegisterAll(tools.Ui, ctx);
        tools.Ui.RegisterGlobalHandler(ToolUninstallUiSchema.RequestKind, async (toolId, arg, _) =>
        {
            if (arg is not ToolUninstallUiSchema schema)
                return ToolUninstallAnswers.Cancel();
            return await GenericToolUninstallDialog.ShowAsync(toolId, schema);
        });
    }
}
