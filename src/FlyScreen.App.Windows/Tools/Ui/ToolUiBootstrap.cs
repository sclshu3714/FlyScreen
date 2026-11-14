using System.Windows;
using FlyScreen.Helpers;
using FlyScreen.Services;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tools.Ui;

/// <summary>扫描工具 UI 程序集贡献者（主程序不含具体工具面板）。</summary>
public static class ToolUiBootstrap
{
    public static void RegisterAll(Window owner, Action<string>? reportStatus = null)
    {
        var tools = ToolService.Instance;
        var ctx = new ToolUiContext
        {
            Owner = owner,
            // 插件只调 ReportStatus；宿主写入日志页（解耦），侧栏仅短提示
            ReportStatus = reportStatus ?? (s =>
            {
                AppLog.RecordStatus(s);
                if (owner is MainWindow mw)
                    mw.StatusText.Text = AppLog.ForStatusBar(s);
            }),
            // 工具独立 Window 不在 MainWindow 资源树内，需显式套用系统样式
            ApplyWindowTheme = o =>
            {
                if (o is Window win)
                    ThemeService.ApplyToChildWindow(win);
            }
        };
        tools.BindUi(ctx);
        ToolUiDiscovery.RegisterAll(tools.Ui, ctx);
    }
}
