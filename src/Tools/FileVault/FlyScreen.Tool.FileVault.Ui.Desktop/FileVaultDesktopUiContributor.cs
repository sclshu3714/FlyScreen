using Avalonia.Controls;
using Avalonia.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.FileVault.Ui.Desktop;

public sealed class FileVaultDesktopUiContributor : IToolUiContributor
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
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var wnd = context.Owner as Window;
                if (wnd == null) return null;
                var folders = await wnd.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = arg as string ?? Loc.T("Tool.FileVault.PickVaultDir"),
                    AllowMultiple = false
                });
                return folders.Count > 0 ? folders[0].Path.LocalPath : null;
            }));

        ui.RegisterHandler(ToolIds.FileVault, "alert", async (arg, ct) =>
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (context.Owner is Window wnd)
                {
                    var dlg = new Window
                    {
                        Title = Loc.T("Tool.FileVault.Name"),
                        Width = 360,
                        Height = 140,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new TextBlock
                        {
                            Text = arg as string ?? "",
                            Margin = new Avalonia.Thickness(16),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        }
                    };
                    await dlg.ShowDialog(wnd);
                }
            });
            return null;
        });
    }
}
