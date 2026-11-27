using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.FileVault.Ui.Maui;

public sealed class FileVaultMauiUiContributor : IToolUiContributor
{
    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        ui.RegisterWorkspaceFactory(ToolIds.FileVault, () =>
        {
            var panel = new FileVaultPanel();
            if (context.ReportStatus != null)
                panel.StatusReporter = context.ReportStatus;
            return panel;
        });

        ui.RegisterHandler(ToolIds.FileVault, "alert", async (arg, ct) =>
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
                await Application.Current!.Windows[0].Page!.DisplayAlert(
                    Loc.T("Tool.FileVault.Name"),
                    arg as string ?? "",
                    Loc.T("Tool.FileVault.Ok")));
            return null;
        });
    }
}
