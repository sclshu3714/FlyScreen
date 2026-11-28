using FlyScreen.Services;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;
using FlyScreen.Ui.Maui;

namespace FlyScreen.AppMaui.Tools.Ui;

public static class ToolUiBootstrap
{
    public static void RegisterAll(Func<INavigation> navigation, Action<string>? reportStatus = null)
    {
        var tools = ToolService.Instance;
        var ctx = new ToolUiContext
        {
            Owner = null,
            // 与 Win 对齐：插件 ReportStatus → 日志页；此前 Maui 未接线导致播放日志全丢
            ReportStatus = reportStatus ?? (s => AppLog.RecordStatus(s)),
            GetNavigation = () => navigation(),
            // 工具独立窗：ForDialog 色板；Maui-Win 另居中并去掉标题栏「投影」
            ApplyWindowTheme = ApplyToolWindowTheme
        };
        tools.BindUi(ctx);
        ToolUiDiscovery.RegisterAll(tools.Ui, ctx);
        tools.Ui.RegisterGlobalHandler(ToolUninstallUiSchema.RequestKind, async (toolId, arg, _) =>
        {
            if (arg is not ToolUninstallUiSchema schema)
                return ToolUninstallAnswers.Cancel();
            return await GenericToolUninstallDialog.ShowAsync(toolId, schema);
        });
    }

    private static void ApplyToolWindowTheme(object target)
    {
        switch (target)
        {
            case Window win:
                if (win.Page is Page page)
                    MauiDialogShell.ApplyForDialogColors(page, MauiDialogShell.ActiveThemeId);
                CenterMauiWindow(win);
#if WINDOWS
                Platforms.Windows.MauiToolWindowChrome.Apply(win);
#endif
                break;
            case ContentPage contentPage:
                // 工具模态页走遮罩弹层（壳外透明），与添加程序 / 投影对话框一致
                MauiDialogShell.PrepareOverlayPage(contentPage, MauiDialogShell.ActiveThemeId);
                break;
            case Page p:
                MauiDialogShell.ApplyForDialogColors(p, MauiDialogShell.ActiveThemeId);
                break;
        }
    }

    private static void CenterMauiWindow(Window window)
    {
        void Place()
        {
            try
            {
#if WINDOWS
                // 原生居中由 MauiToolWindowChrome 负责
                return;
#else
                var display = DeviceDisplay.MainDisplayInfo;
                var density = display.Density <= 0 ? 1 : display.Density;
                var screenW = display.Width / density;
                var screenH = display.Height / density;
                window.X = Math.Max(0, (screenW - window.Width) / 2);
                window.Y = Math.Max(0, (screenH - window.Height) / 2);
#endif
            }
            catch
            {
                // ignore
            }
        }

        window.HandlerChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Place);
        window.Created += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            Place();
            window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), Place);
        });
        Place();
    }
}
