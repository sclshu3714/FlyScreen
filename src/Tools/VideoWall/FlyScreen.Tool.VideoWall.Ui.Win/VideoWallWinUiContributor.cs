using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

public sealed class VideoWallWinUiContributor : IToolUiContributor
{
    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        ui.RegisterWorkspaceFactory(ToolIds.VideoWall, () =>
        {
            try
            {
                var panel = new VideoWallPanel();
                if (context.ReportStatus != null)
                    panel.StatusReporter = context.ReportStatus;
                return panel;
            }
            catch (Exception ex)
            {
                return new TextBlock
                {
                    Text = Loc.T("Tool.VideoWall.BackendUnavailable") + "\n" + ex.Message,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8),
                    FontSize = 13
                };
            }
        });
    }
}