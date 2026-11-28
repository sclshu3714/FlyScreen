using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.FileVault.Ui.Maui;

public sealed class FileVaultMauiUiContributor : IToolUiContributor
{
    private static Window? _vaultWindow;

    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        ui.RegisterWorkspaceFactory(ToolIds.FileVault, () =>
            new Label { Text = Loc.T("Tool.FileVault.Name"), Opacity = 0.01, Margin = new Thickness(12) });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiShowWindow, async (_, _) =>
        {
            await MainThread.InvokeOnMainThreadAsync(() => ShowVaultWindow(context));
            return null;
        });

        ui.RegisterHandler(ToolIds.FileVault, Module.UiCloseWindow, async (_, _) =>
        {
            await MainThread.InvokeOnMainThreadAsync(CloseVaultWindow);
            return null;
        });

        ui.RegisterHandler(ToolIds.FileVault, "alert", async (arg, ct) =>
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = _vaultWindow?.Page
                           ?? Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page != null)
                    await page.DisplayAlertAsync(
                        Loc.T("Tool.FileVault.Name"),
                        arg as string ?? "",
                        Loc.T("Tool.FileVault.Ok"));
            });
            return null;
        });
    }

    private static void ShowVaultWindow(IToolUiContext context)
    {
        if (_vaultWindow != null)
        {
            var listed = Application.Current?.Windows.Contains(_vaultWindow) == true;
            if (_vaultWindow.Handler?.PlatformView is null || !listed)
            {
                try
                {
                    if (_vaultWindow.Handler?.PlatformView is not null)
                        Application.Current?.CloseWindow(_vaultWindow);
                }
                catch { /* ignore */ }
                _vaultWindow = null;
            }
            else
            {
                try
                {
                    Application.Current?.ActivateWindow(_vaultWindow);
                    ApplyChrome(context, _vaultWindow, ResolveDrag(_vaultWindow));
                    return;
                }
                catch
                {
                    _vaultWindow = null;
                }
            }
        }

        Window? win = null;
        try
        {
            var panel = new FileVaultPanel { VerticalOptions = LayoutOptions.Fill, HorizontalOptions = LayoutOptions.Fill };
            var page = new ContentPage
            {
                Title = Loc.T("Tool.FileVault.Name"),
                Content = panel,
                Padding = 0
            };
            NavigationPage.SetHasNavigationBar(page, false);
            win = new Window(page)
            {
                Title = Loc.T("Tool.FileVault.Name"),
                Width = 980,
                Height = 640,
                MinimumWidth = 720,
                MinimumHeight = 480
            };
            win.Destroying += async (_, _) =>
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

            Application.Current?.OpenWindow(win);
            _vaultWindow = win;

            try
            {
                panel.ApplyTheme();
                panel.MountCaptionTabs(win);
                ApplyChrome(context, win, panel.TitleDragRegion);
            }
            catch
            {
                // 主题/Chrome 失败不应挡住已打开的窗
            }

            // 短延迟补枪（宿主 MauiFileVaultWindowChrome 内部也有 100/300ms）
            win.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
            {
                try
                {
                    panel.MountCaptionTabs(win);
                    ApplyChrome(context, win, panel.TitleDragRegion);
                    panel.ApplyTheme();
                }
                catch { /* ignore */ }
            });
            win.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), () =>
            {
                try
                {
                    panel.MountCaptionTabs(win);
                    ApplyChrome(context, win, panel.TitleDragRegion);
                    panel.ApplyTheme();
                }
                catch { /* ignore */ }
            });
        }
        catch (Exception ex)
        {
            _vaultWindow = null;
            if (win != null)
            {
                try { Application.Current?.CloseWindow(win); } catch { /* ignore */ }
            }

            try
            {
                var hostPage = Application.Current?.Windows.FirstOrDefault()?.Page;
                _ = hostPage?.DisplayAlertAsync(
                    Loc.T("Tool.FileVault.Name"),
                    ex.Message,
                    Loc.T("Tool.FileVault.Ok"));
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>宿主清假标题栏 + 工具侧挂拖窗。</summary>
    private static void ApplyChrome(IToolUiContext context, Window win, View? drag)
    {
        context.ApplyWindowTheme?.Invoke(win);
        FileVaultMauiTitleChrome.Apply(win, drag);
    }

    private static View? ResolveDrag(Window win)
    {
        if ((win.Page as ContentPage)?.Content is not { } c) return null;
        return c.GetType().GetProperty("TitleDragRegion")?.GetValue(c) as View;
    }

    private static void CloseVaultWindow()
    {
        var win = _vaultWindow;
        _vaultWindow = null;
        if (win != null && Application.Current != null)
        {
            try { Application.Current.CloseWindow(win); }
            catch { /* ignore */ }
        }
    }
}
