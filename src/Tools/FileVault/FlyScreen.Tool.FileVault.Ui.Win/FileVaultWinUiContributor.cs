using System.Windows;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;
using Microsoft.Win32;

namespace FlyScreen.Tool.FileVault.Ui.Win;

public sealed class FileVaultWinUiContributor : IToolUiContributor
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

        ui.RegisterHandler(ToolIds.FileVault, Module.UiPickFolder, async (arg, ct) =>
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dlg = new OpenFolderDialog
                {
                    Title = arg as string ?? Loc.T("Tool.FileVault.PickVaultDir")
                };
                return dlg.ShowDialog() == true ? dlg.FolderName : null;
            });
        });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiPickFiles, async (_, ct) =>
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dlg = new OpenFileDialog
                {
                    Multiselect = true,
                    Title = Loc.T("Tool.FileVault.Import")
                };
                return dlg.ShowDialog() == true ? dlg.FileNames.ToList() : null;
            });
        });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiPromptPassword, async (arg, ct) =>
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
                FileVaultPasswordDialog.Show(context.Owner as Window, arg as string));
        });

        ui.RegisterHandler(ToolIds.FileVault, "alert", async (arg, ct) =>
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(
                    context.Owner as Window,
                    arg as string ?? "",
                    Loc.T("Tool.FileVault.Name"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
            return null;
        });
    }
}
