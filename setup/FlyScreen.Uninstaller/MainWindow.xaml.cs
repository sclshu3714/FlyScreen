using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FlyScreen.Uninstaller.Services;

namespace FlyScreen.Uninstaller;

public partial class MainWindow : Window
{
    private readonly UninstallContext _ctx;
    private readonly bool _silent;
    private bool _busy;
    private bool _done;

    public MainWindow(UninstallContext ctx, bool silent)
    {
        InitializeComponent();
        _ctx = ctx;
        _silent = silent;

        Title = $"卸载 {_ctx.ProductName}";
        TitleRun.Text = $"卸载 {_ctx.ProductName}";
        VersionRun.Text = string.IsNullOrWhiteSpace(_ctx.Version) ? "" : $"v{_ctx.Version}";
        PathText.Text = _ctx.InstallDir;

        if (!_ctx.Found)
        {
            StatusText.Text = "未找到安装目录，仍可清理注册表与快捷方式。";
            ProgressPanel.Visibility = Visibility.Visible;
        }

        if (_silent)
            Loaded += async (_, _) => await RunUninstallAsync().ConfigureAwait(true);
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d)
        {
            while (d != null)
            {
                if (d is System.Windows.Controls.Button or System.Windows.Controls.CheckBox)
                    return;
                d = VisualTreeHelper.GetParent(d);
            }
        }
        try { DragMove(); } catch { /* ignore */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_done)
            UninstallService.ScheduleSelfDelete();
        Close();
    }

    private void ApplyProgress(ProgressReport report)
    {
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = report.Percent;
        var pct = (int)Math.Round(report.Percent);
        StatusText.Text = string.IsNullOrWhiteSpace(report.Message)
            ? $"{pct}%"
            : $"{report.Message}  {pct}%";
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            UninstallService.ScheduleSelfDelete();
            Close();
            return;
        }

        await RunUninstallAsync().ConfigureAwait(true);
    }

    private async Task RunUninstallAsync()
    {
        if (_busy || _done) return;

        var keepData = KeepDataBox.IsChecked == true;

        _busy = true;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        UninstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        KeepDataBox.IsEnabled = false;

        try
        {
            var progress = new Progress<ProgressReport>(ApplyProgress);
            var service = new UninstallService();
            await service.UninstallAsync(_ctx, keepData, progress).ConfigureAwait(true);

            _done = true;
            _busy = false;
            ApplyProgress(ProgressReport.Create(100, "卸载完成"));
            StatusText.Foreground = (Brush)FindResource("AccentBrush");

            CancelButton.Visibility = Visibility.Collapsed;
            UninstallButton.Content = "完成";
            UninstallButton.Style = (Style)FindResource("PrimaryButton");
            UninstallButton.IsEnabled = true;

            if (_silent)
            {
                UninstallService.ScheduleSelfDelete();
                Close();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "卸载失败：" + ex.Message;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            UninstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            KeepDataBox.IsEnabled = true;
            _busy = false;
        }
    }
}
