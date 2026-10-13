using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using FlyScreen.Services;
using FlyScreen.Services.Update;

namespace FlyScreen;

public partial class HelpWindow : Window
{
    private readonly AppHost _host;
    private readonly GitHubUpdateService _updater = new();
    private UpdateCheckResult? _lastCheck;

    public HelpWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        HelpRich.Document = BuildHelpDocument();
    }

    private static FlowDocument BuildHelpDocument()
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            LineHeight = 22
        };

        doc.Blocks.Add(DocTitle("玄鸟 — 跨电脑键鼠与剪贴板共享"));
        doc.Blocks.Add(Para("最多支持 4 台电脑共用一套键鼠；统一剪贴板与文件传输。建议以管理员身份运行。"));

        doc.Blocks.Add(Heading("快速开始"));
        doc.Blocks.Add(Numbered(
            "多台电脑安装并启动玄鸟（版本尽量一致）。",
            "「基础设置」中填写相同的安全密钥。",
            "「网络设置」选择实际通信网卡，确认局域网发现能看到对方。",
            "将对方加入「电脑布局」，按真实摆放调整 1×4 / 2×2，点「应用」。",
            "状态显示「双向已连接」后，鼠标移出屏幕边缘即可切换控制。布局会同步到其它已连接电脑。"));

        doc.Blocks.Add(Heading("剪贴板与文件"));
        doc.Blocks.Add(Bullets(
            "文本 / HTML / RTF / CSV / 截图：复制只宣告，鼠标进入某台后再拉取到该机。",
            "复制文件：鼠标移到目标机后后台接收，完成后才可粘贴；接收中可继续操作。",
            "「文件传输」→「直接传输…」可推送到对方桌面、文档、下载、收件箱或自定义路径。",
            "「清除已完成」只删接收方收件箱缓存，不删发送方本地文件。"));

        doc.Blocks.Add(Heading("常用选项"));
        doc.Blocks.Add(Bullets(
            "轻松鼠标：移出屏幕边缘切换电脑。",
            "环形鼠标：布局首尾相连，仅在相邻槽位有电脑时切换。",
            "控制远端时隐藏本机边缘光标：只显示当前操作机的光标。",
            "开机自动启动：通过任务计划程序以管理员权限登录后启动。",
            "程序单实例：再次启动会激活已有窗口（含托盘隐藏）。"));

        doc.Blocks.Add(Heading("快捷键"));
        doc.Blocks.Add(Para("Ctrl+Alt+M 打开设置　　Ctrl+Alt+R 重新连接　　Ctrl+Alt+Q 退出"));
        doc.Blocks.Add(Para("Ctrl+Alt+F1–F4（或 1–4）切换到对应机器（可在高级选项中更改）"));

        doc.Blocks.Add(Heading("网络与防火墙"));
        doc.Blocks.Add(Para("请允许 TCP（默认 15155）与 UDP 发现（默认 15156）在专用网络通过。"));
        doc.Blocks.Add(Para("配置目录：%AppData%\\FlyScreen\\"));

        doc.Blocks.Add(Heading("更新"));
        doc.Blocks.Add(Para("点击下方「检查更新 / 自动更新」，通过 GitHub Release 获取新版本："));
        doc.Blocks.Add(Para(GitHubUpdateService.ReleasesUrl));

        doc.Blocks.Add(Heading("连接诊断"));
        doc.Blocks.Add(Para("若无法双向连接：先点「一键检查」查看网卡、发现、防火墙与出入站状态；再点「一键修复」自动放行防火墙、重启网络并重连。双方都执行效果更佳。"));

        return doc;
    }

    private static Paragraph DocTitle(string text) => new(new Run(text)
    {
        FontSize = 16,
        FontWeight = FontWeights.SemiBold
    })
    { Margin = new Thickness(0, 0, 0, 10) };

    private static Paragraph Heading(string text) => new(new Run(text)
    {
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x29, 0x37))
    })
    { Margin = new Thickness(0, 12, 0, 6) };

    private static Paragraph Para(string text) => new(new Run(text)
    {
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x37, 0x41, 0x51))
    })
    { Margin = new Thickness(0, 0, 0, 4) };

    private static List Numbered(params string[] items)
    {
        var list = new List { MarkerStyle = TextMarkerStyle.Decimal, Margin = new Thickness(8, 0, 0, 4) };
        foreach (var item in items)
            list.ListItems.Add(new ListItem(Para(item)));
        return list;
    }

    private static List Bullets(params string[] items)
    {
        var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(8, 0, 0, 4) };
        foreach (var item in items)
            list.ListItems.Add(new ListItem(Para(item)));
        return list;
    }

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            ActionStatus.Text = "正在检查 GitHub Release…";
            var result = await _updater.CheckAsync().ConfigureAwait(true);
            _lastCheck = result;
            ApplyUpdateBtn.IsEnabled = result is { Success: true, UpdateAvailable: true };
            ActionStatus.Text = result.Message;
            if (result.Success && result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.ReleaseNotes))
            {
                ShowDiag($"版本 {result.RemoteVersion}\n\n{result.ReleaseNotes}");
            }
            else if (!result.Success)
            {
                ShowDiag(result.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCheck is not { Success: true, UpdateAvailable: true })
        {
            ActionStatus.Text = "请先点击「检查更新」。";
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"将下载并安装 { _lastCheck.RemoteVersion }，程序会自动重启。\n\n资源：{_lastCheck.AssetName ?? "（未命名）"}\n是否继续？",
            "自动更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<double>(p => ActionStatus.Text = $"正在下载更新… {p:0}%");
            var apply = await _updater.DownloadAndApplyAsync(_lastCheck, progress).ConfigureAwait(true);
            ActionStatus.Text = apply.Message;
            ShowDiag(apply.Message);
            if (apply.ShouldExit)
            {
                await Task.Delay(400).ConfigureAwait(true);
                Application.Current.Shutdown();
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DiagCheck_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            ActionStatus.Text = "正在检查连接…";
            var diag = new ConnectionDiagnostics(_host);
            var report = await diag.CheckAsync().ConfigureAwait(true);
            ActionStatus.Text = report.Summary;
            ShowDiag(report.FormatText());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DiagFix_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var diag = new ConnectionDiagnostics(_host);
            var progress = new Progress<string>(s => ActionStatus.Text = s);
            var report = await diag.FixAsync(progress).ConfigureAwait(true);
            ActionStatus.Text = report.Summary;
            ShowDiag(report.FormatText());

            // 通知主窗口刷新布局/状态（若从发现补了槽位）
            if (Owner is MainWindow mw)
                mw.ReloadAfterDiagnostics();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowDiag(string text)
    {
        DiagOutput.Visibility = Visibility.Visible;
        DiagOutput.Text = text;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
