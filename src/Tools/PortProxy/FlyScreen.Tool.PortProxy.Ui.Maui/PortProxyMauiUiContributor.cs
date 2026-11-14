using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy.Ui.Maui;

public sealed class PortProxyMauiUiContributor : IToolUiContributor
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
                return new Label
                {
                    Text = Loc.T("Tool.PortProxy.NotReady") + "\n" + ex.Message,
                    LineBreakMode = LineBreakMode.WordWrap,
                    Margin = new Thickness(8),
                    FontSize = 13
                };
            }
        });
    }
}
