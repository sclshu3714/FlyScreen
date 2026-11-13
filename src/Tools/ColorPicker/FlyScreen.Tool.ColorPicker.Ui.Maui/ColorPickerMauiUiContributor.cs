using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.ColorPicker.Ui.Maui;

public sealed class ColorPickerMauiUiContributor : IToolUiContributor
{
    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        ui.RegisterWorkspaceFactory(ToolIds.ColorPicker, () =>
        {
            var panel = new ColorPickerPanel();
            if (context.ReportStatus != null)
                panel.StatusReporter = context.ReportStatus;
            return panel;
        });
    }
}
