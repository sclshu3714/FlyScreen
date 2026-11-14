using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy.Ui.Win;

public sealed class PortProxyWinUiContributor : IToolUiContributor
{
    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        ui.RegisterWorkspaceFactory(ToolIds.PortProxy, () =>
        {
            try
            {
                var panel = new PortProxyPanel();
                if (context.ReportStatus != null)
                    panel.StatusReporter = context.ReportStatus;
                return panel;
            }
            catch (Exception ex)
            {
                return new TextBlock
                {
                    Text = Loc.T("Tool.PortProxy.NotReady") + "\n" + ex.Message,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8),
                    FontSize = 13
                };
            }
        });
    }
}
