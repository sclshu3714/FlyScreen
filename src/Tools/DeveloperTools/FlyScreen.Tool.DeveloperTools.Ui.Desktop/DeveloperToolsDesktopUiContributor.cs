using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.DeveloperTools.Ui.Desktop;

public sealed class DeveloperToolsDesktopUiContributor : IToolUiContributor
{
    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        ui.RegisterWorkspaceFactory(ToolIds.DeveloperTools, () =>
        {
            var panel = new DeveloperToolsPanel();
            if (context.ReportStatus != null)
                panel.StatusReporter = context.ReportStatus;
            return panel;
        });
    }
}
