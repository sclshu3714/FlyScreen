using Avalonia.Controls;
using Avalonia.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.FileVault.Ui.Desktop;

public sealed class FileVaultDesktopUiContributor : IToolUiContributor
{
    private static Window? _vaultWindow;

    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        ui.RegisterWorkspaceFactory(ToolIds.FileVault, () =>
            new TextBlock { Text = Loc.T("Tool.FileVault.Name"), Opacity = 0.01, Margin = new Avalonia.Thickness(12) });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiShowWindow, async (_, _) =>
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowVaultWindow(context));
            return null;
        });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiCloseWindow, async (_, _) =>
        {
            await Dispatcher.UIThread.InvokeAsync(CloseVaultWindow);
            return null;
        });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiPickFolder, async (arg, ct) =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var wnd = _vaultWindow ?? context.Owner as Window;
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
                var owner = _vaultWindow ?? context.Owner as Window;
                if (owner == null) return;
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
                context.ApplyWindowTheme?.Invoke(dlg);
                await dlg.ShowDialog(owner);
            });
            return null;
        });
    }

    private static void ShowVaultWindow(IToolUiContext context)
    {
        if (_vaultWindow != null)
        {
            _vaultWindow.Activate();
            _vaultWindow.Focus();
            context.ApplyWindowTheme?.Invoke(_vaultWindow);
            return;
        }

        var panel = new FileVaultPanel();
        var win = new Window
        {
            Title = Loc.T("Tool.FileVault.Name"),
            Width = 980,
            Height = 640,
            MinWidth = 720,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = panel,
            // Tab 即标题栏
            SystemDecorations = SystemDecorations.BorderOnly,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
            ExtendClientAreaTitleBarHeightHint = 0
        };
        if (context.Owner is Window owner && owner.IsVisible)
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        win.Opened += (_, _) =>
        {
            context.ApplyWindowTheme?.Invoke(win);
            panel.ApplyTheme();
            CenterIfNeeded(win, context.Owner as Window);
        };
        win.Closed += async (_, _) =>
        {
            if (ReferenceEquals(_vaultWindow, win))
                _vaultWindow = null;
            try
            {
                var svc = ToolService.Instance.GetToolService<IFileVaultService>();
                if (svc?.IsUnlocked == true)
                    await svc.LockAsync();
            }
            catch { /* ignore */ }
        };
        _vaultWindow = win;
        context.ApplyWindowTheme?.Invoke(win);
        panel.ApplyTheme();
        if (context.Owner is Window o)
            win.Show(o);
        else
            win.Show();
        win.Activate();
    }

    private static void CenterIfNeeded(Window win, Window? owner)
    {
        try
        {
            if (owner is { IsVisible: true })
            {
                win.Position = new Avalonia.PixelPoint(
                    (int)(owner.Position.X + (owner.Width - win.Width) / 2),
                    (int)(owner.Position.Y + (owner.Height - win.Height) / 2));
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void CloseVaultWindow()
    {
        var win = _vaultWindow;
        _vaultWindow = null;
        win?.Close();
    }
}
