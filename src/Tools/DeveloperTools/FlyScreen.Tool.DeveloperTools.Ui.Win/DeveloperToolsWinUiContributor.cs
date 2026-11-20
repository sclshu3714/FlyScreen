using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.DeveloperTools.Ui.Win;

public sealed class DeveloperToolsWinUiContributor : IToolUiContributor
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
