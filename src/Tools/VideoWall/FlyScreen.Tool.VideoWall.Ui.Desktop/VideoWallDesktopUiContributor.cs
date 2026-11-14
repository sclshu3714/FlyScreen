using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall.Ui.Desktop;

public sealed class VideoWallDesktopUiContributor : IToolUiContributor
{
    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        ui.RegisterWorkspaceFactory(ToolIds.VideoWall, () =>
        {
            var panel = new VideoWallPanel();
            if (context.ReportStatus != null)
                panel.StatusReporter = context.ReportStatus;
            return panel;
        });
    }
}