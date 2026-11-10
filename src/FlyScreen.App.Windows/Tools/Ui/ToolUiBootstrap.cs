using System.Windows;
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
            ReportStatus = reportStatus ?? (s =>
            {
                if (owner is MainWindow mw)
                    mw.StatusText.Text = s;
            })
        };
        tools.BindUi(ctx);
        ToolUiDiscovery.RegisterAll(tools.Ui, ctx);
    }
}
