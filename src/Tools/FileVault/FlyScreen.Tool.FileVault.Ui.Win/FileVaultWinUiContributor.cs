using System.Windows;
using System.Windows.Shell;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;
using FlyScreen.Ui.Theme.Wpf;
using Microsoft.Win32;

namespace FlyScreen.Tool.FileVault.Ui.Win;

public sealed class FileVaultWinUiContributor : IToolUiContributor
{
    private static Window? _vaultWindow;

    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        // 弹窗模式：不再嵌入 ToolsWorkspaceHost
        ui.RegisterWorkspaceFactory(ToolIds.FileVault, () =>
            new System.Windows.Controls.TextBlock
            {
                Text = Loc.T("Tool.FileVault.Name"),
                Margin = new Thickness(12),
                Opacity = 0.01
            });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiShowWindow, async (_, _) =>
        {
            await Application.Current.Dispatcher.InvokeAsync(() => ShowVaultWindow(context));
            return null;
        });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiCloseWindow, async (_, _) =>
        {
            await Application.Current.Dispatcher.InvokeAsync(CloseVaultWindow);
            return null;
        });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiPickFolder, async (arg, ct) =>
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dlg = new OpenFolderDialog
                {
                    Title = arg as string ?? Loc.T("Tool.FileVault.PickVaultDir")
                };
                return dlg.ShowDialog(_vaultWindow ?? context.Owner as Window) == true
                    ? dlg.FolderName
                    : null;
            });
        });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiPickFiles, async (_, ct) =>
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dlg = new OpenFileDialog
                {
                    Multiselect = true,
                    Title = Loc.T("Tool.FileVault.ImportFile")
                };
                return dlg.ShowDialog(_vaultWindow ?? context.Owner as Window) == true
                    ? dlg.FileNames.ToList()
                    : null;
            });
        });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiPromptPassword, async (arg, ct) =>
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
                FileVaultPasswordDialog.Show(_vaultWindow ?? context.Owner as Window, arg as string));
        });

        ui.RegisterHandler(ToolIds.FileVault, "alert", async (arg, ct) =>
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(
                    _vaultWindow ?? context.Owner as Window,
                    arg as string ?? "",
                    Loc.T("Tool.FileVault.Name"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
            return null;
        });
    }

    private static void ShowVaultWindow(IToolUiContext context)
    {
        if (_vaultWindow != null)
        {
            if (_vaultWindow.WindowState == WindowState.Minimized)
                _vaultWindow.WindowState = WindowState.Normal;
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
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = panel,
            ShowInTaskbar = true,
            // Tab 即标题栏：无系统标题文案区
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.CanResize
        };
        WindowChrome.SetWindowChrome(win, new WindowChrome
        {
            CaptionHeight = 40,
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(4),
            UseAeroCaptionButtons = false
        });
        win.SourceInitialized += (_, _) => CenterOnOwnerOrScreen(win, context.Owner as Window);
        win.Loaded += (_, _) =>
        {
            context.ApplyWindowTheme?.Invoke(win);
            WpfDialogTheme.ApplyTo(panel);
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
        WpfDialogTheme.ApplyTo(panel);
        win.Show();
        CenterOnOwnerOrScreen(win, context.Owner as Window);
        win.Activate();
        win.Topmost = true;
        win.Topmost = false;
        win.Focus();
    }

    private static void CenterOnOwnerOrScreen(Window win, Window? owner)
    {
        try
        {
            if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
            {
                var left = owner.Left + (owner.ActualWidth - win.Width) / 2;
                var top = owner.Top + (owner.ActualHeight - win.Height) / 2;
                win.Left = left;
                win.Top = top;
                return;
            }

            var work = SystemParameters.WorkArea;
            win.Left = work.Left + (work.Width - win.Width) / 2;
            win.Top = work.Top + (work.Height - win.Height) / 2;
        }
        catch
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private static void CloseVaultWindow()
    {
        var win = _vaultWindow;
        _vaultWindow = null;
        win?.Close();
    }
}
