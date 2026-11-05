using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FlyScreen.Uninstaller.Services;
using Microsoft.Win32;

namespace FlyScreen.Uninstaller;

public partial class ModifyWindow : Window
{
    private readonly UninstallContext _ctx;
    private readonly ModifyUpdateService _updater = new();
    private bool _busy;
    private ModifyUpdateService.CheckResult? _pending;

    public ModifyWindow(UninstallContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;
        Title = $"修改 {_ctx.ProductName}";
        TitleRun.Text = $"修改 {_ctx.ProductName}";
        VersionRun.Text = string.IsNullOrWhiteSpace(_ctx.Version) ? "" : $"v{_ctx.Version}";
        PathText.Text = string.IsNullOrWhiteSpace(_ctx.InstallDir) ? "—" : _ctx.InstallDir;
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d)
        {
            while (d != null)
            {
                if (d is System.Windows.Controls.Button)
                    return;
                d = VisualTreeHelper.GetParent(d);
            }
        }
        try { DragMove(); } catch { /* ignore */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Close();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_pending is not { UpdateAvailable: true })
        {
            SetBusy(true);
            StatusText.Text = "正在检查更新…";
            StatusText.Foreground = (Brush)FindResource("MutedBrush");
            try
            {
                var result = await Task.Run(() => _updater.Check(_ctx)).ConfigureAwait(true);
                _pending = result.UpdateAvailable ? result : null;
                StatusText.Text = result.Message;
                StatusText.Foreground = (Brush)FindResource(result.Success ? "AccentBrush" : "DangerBrush");
                UpdateButton.Content = result.UpdateAvailable
                    ? $"更新到 v{result.RemoteVersion}"
                    : "检查并更新";
            }
            finally
            {
                SetBusy(false);
            }

            return;
        }

        var check = _pending;
        var go = MessageBox.Show(this,
            $"将下载并安装 {check.RemoteVersion}。\n\n{check.AssetName}\n\n安装程序启动后本窗口会关闭。是否继续？",
            "更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (go != MessageBoxResult.Yes) return;

        SetBusy(true);
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                StatusText.Text = $"正在下载更新… {p:0}%";
            });
            var path = await _updater.DownloadAsync(check.DownloadUrl!, check.AssetName!, progress, CancellationToken.None)
                .ConfigureAwait(true);
            StatusText.Text = "正在启动安装程序…";
            ModifyUpdateService.LaunchSetup(path);
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "更新失败：" + ex.Message;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButton.IsEnabled = !busy;
        RepairShortcutButton.IsEnabled = !busy;
        RepairRegistryButton.IsEnabled = !busy;
        OpenFolderButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
    }

    private void RepairShortcuts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = ResolveExe();
            if (exe == null)
            {
                StatusText.Text = "未找到主程序，无法创建快捷方式。";
                StatusText.Foreground = (Brush)FindResource("DangerBrush");
                return;
            }

            var workDir = Path.GetDirectoryName(exe) ?? _ctx.InstallDir;
            ModifyService.RepairShortcuts(_ctx.ProductName, exe, workDir);
            StatusText.Text = "已修复桌面与开始菜单快捷方式。";
            StatusText.Foreground = (Brush)FindResource("AccentBrush");
        }
        catch (Exception ex)
        {
            StatusText.Text = "修复快捷方式失败：" + ex.Message;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
    }

    private void RepairRegistry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = ResolveExe();
            ModifyService.RepairUninstallRegistry(_ctx, exe);
            StatusText.Text = "已刷新「应用和功能」中的卸载/修改信息。";
            StatusText.Foreground = (Brush)FindResource("AccentBrush");
        }
        catch (Exception ex)
        {
            StatusText.Text = "修复卸载信息失败：" + ex.Message;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_ctx.InstallDir))
            {
                StatusText.Text = "安装目录不存在。";
                StatusText.Foreground = (Brush)FindResource("DangerBrush");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _ctx.InstallDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开目录：" + ex.Message;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var win = new MainWindow(_ctx, silent: false);
        win.Show();
        Close();
    }

    private string? ResolveExe()
    {
        if (string.IsNullOrWhiteSpace(_ctx.InstallDir) || !Directory.Exists(_ctx.InstallDir))
            return null;

        var preferred = Path.Combine(_ctx.InstallDir, _ctx.ExeName);
        if (File.Exists(preferred)) return preferred;

        try
        {
            return Directory.EnumerateFiles(_ctx.InstallDir, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    return n.Equals("FlyScreen", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("FlyScreen", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch
        {
            return null;
        }
    }
}
