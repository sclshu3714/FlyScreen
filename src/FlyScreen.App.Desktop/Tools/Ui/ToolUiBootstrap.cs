using Avalonia.Controls;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.DesktopUi.Tools.Ui;

public static class ToolUiBootstrap
{
    public static void RegisterAll(Window owner, Action<string> setStatus)
    {
        var tools = ToolService.Instance;
        var ctx = new ToolUiContext
        {
            Owner = owner,
            ReportStatus = setStatus
        };
        tools.BindUi(ctx);
        ToolUiDiscovery.RegisterAll(tools.Ui, ctx);
        tools.Ui.RegisterGlobalHandler(ToolUninstallUiSchema.RequestKind, async (toolId, arg, _) =>
        {
            if (arg is not ToolUninstallUiSchema schema)
                return ToolUninstallAnswers.Cancel();
            return await GenericToolUninstallDialog.ShowAsync(ctx.Owner as Window, toolId, schema);
        });
    }
}
