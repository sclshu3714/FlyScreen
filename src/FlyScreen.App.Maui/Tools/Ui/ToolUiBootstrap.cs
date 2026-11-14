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
            ReportStatus = reportStatus,
            GetNavigation = () => navigation()
        };
        tools.BindUi(ctx);
        ToolUiDiscovery.RegisterAll(tools.Ui, ctx);
    }
}
