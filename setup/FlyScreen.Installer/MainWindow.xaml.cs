using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyScreen.Installer.Services;
using Microsoft.Win32;

namespace FlyScreen.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerManifest _manifest;
    private readonly string? _payloadZip;
    private readonly string? _licensePath;
    private bool _busy;
    private bool _finished;
    private string? _finishedInstallDir;
    private CancellationTokenSource? _installCts;
    private double _lockedWidth;
    private double _lockedHeight;
    private bool _sizeLocked;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTitleIcon();
        _manifest = ManifestLoader.Load();
        _payloadZip = ManifestLoader.FindPayloadZip();
        _licensePath = ManifestLoader.FindLicenseText();

        Title = $"安装 {_manifest.ProductName}";
        TitleRun.Text = $"安装 {_manifest.ProductName}";
        VersionRun.Text = VersionResolver.FormatDisplay(_manifest.Version);

        PathBox.Text = string.IsNullOrWhiteSpace(_manifest.DefaultInstallDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "XuanNiao")
            : _manifest.DefaultInstallDir;

        DesktopShortcutBox.IsChecked = _manifest.CreateDesktopShortcut;
        StartMenuShortcutBox.IsChecked = _manifest.CreateStartMenuShortcut;

        var images = ManifestLoader.ResolveCarouselImages(_manifest).Distinct().ToList();
        Carousel.SetImages(images);

        UpdateSpaceInfo();
        UpdateInstallEnabled();

        if (_payloadZip == null)
        {
            StatusText.Text = "未找到安装数据（Payload/payload.zip）。请通过发布工具生成安装包。";
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>自绘标题栏不走系统图标，标题前和任务栏都用同一只鸟标。</summary>
    private void ApplyTitleIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info?.Stream == null) return;

            var decoder = BitmapDecoder.Create(info.Stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .OrderBy(f => Math.Abs(f.PixelWidth - 32))
                .FirstOrDefault();
            if (frame == null) return;
            if (frame.CanFreeze) frame.Freeze();
            Icon = frame;
            TitleIcon.Source = frame;
        }
        catch
        {
            /* 图标缺失时仍可安装 */
        }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject src && IsInteractiveElement(src))
            return;
        try { DragMove(); } catch { /* ignore */ }
    }

    private static bool IsInteractiveElement(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button or CheckBox or TextBox or PasswordBox or ComboBox or Slider or Hyperlink)
                return true;
            if (d is System.Windows.Shapes.Ellipse) return true;
            if (d is FrameworkElement { TemplatedParent: Button or CheckBox }) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_sizeLocked) return;
        UpdateLayout();
        _lockedWidth = ActualWidth > 0 ? ActualWidth : Width;
        _lockedHeight = ActualHeight;
        if (_lockedHeight < 100) return;
        LockWindowSize();
        ContentRendered -= Window_ContentRendered;
    }

    private void LockWindowSize()
    {
        _sizeLocked = true;
        SizeToContent = SizeToContent.Manual;
        MinWidth = MaxWidth = Width = _lockedWidth;
        MinHeight = Height = _lockedHeight;
        MaxHeight = double.PositiveInfinity;
    }

    private void ApplyBaseWindowSize()
    {
        if (!_sizeLocked) return;
        SizeToContent = SizeToContent.Manual;
        Width = _lockedWidth;
        Height = _lockedHeight;
        MinHeight = _lockedHeight;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            if (!ConfirmDialog.Ask(this, "取消安装",
                    "安装正在进行，关闭窗口将终止并回滚。是否继续？",
                    "终止安装", "继续安装", ConfirmDialogKind.Warning, defaultYes: false))
                return;
            _installCts?.Cancel();
        }
        Close();
    }

    private void Agree_Changed(object sender, RoutedEventArgs e) => UpdateInstallEnabled();

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetMoreExpanded(MorePanel.Visibility != Visibility.Visible);
    }

    private void SetMoreExpanded(bool open)
    {
        MorePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        MoreLabel.Text = open ? "收起" : "更多";
        MoreIcon.Data = Geometry.Parse(open ? "M1,5 L5,1 L9,5" : "M1,1 L5,5 L9,1");
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, RootBorder);

        if (!_sizeLocked) return;
        if (!open)
        {
            ApplyBaseWindowSize();
            return;
        }

        UpdateLayout();
        var extra = MorePanel.ActualHeight + MorePanel.Margin.Top;
        if (extra < 1) extra = MorePanel.DesiredSize.Height + 14;
        SizeToContent = SizeToContent.Manual;
        MinHeight = 0;
        Width = _lockedWidth;
        Height = _lockedHeight + extra;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择安装目录",
            InitialDirectory = Directory.Exists(PathBox.Text)
                ? PathBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dlg.ShowDialog(this) == true)
            PathBox.Text = dlg.FolderName;
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateSpaceInfo();

    private void LicenseLink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(_licensePath) && File.Exists(_licensePath))
        {
            var win = new Window
            {
                Title = "许可协议",
                Owner = this,
                Width = 560,
                Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontFamily = (FontFamily)FindResource("UiFont"),
                Content = new TextBox
                {
                    Text = File.ReadAllText(_licensePath),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(16),
                    BorderThickness = new Thickness(0),
                    FontSize = 13
                }
            };
            win.ShowDialog();
            return;
        }

        var url = string.IsNullOrWhiteSpace(_manifest.LicenseUrl)
            ? "https://github.com/sclshu3714/FlyScreen"
            : _manifest.LicenseUrl;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfirmDialog.Alert(this, "提示", "无法打开许可链接：\n" + ex.Message, ConfirmDialogKind.Info);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        // 安装中点击 = 取消安装
        if (_busy)
        {
            _installCts?.Cancel();
            StatusText.Text = "正在取消…";
            return;
        }

        if (AgreeBox.IsChecked != true)
        {
            ConfirmDialog.Alert(this, "提示", "请先阅读并同意许可协议。", ConfirmDialogKind.Info);
            return;
        }

        if (_payloadZip == null)
        {
            ConfirmDialog.Alert(this, "错误", "缺少 payload.zip，无法安装。", ConfirmDialogKind.Error);
            return;
        }

        if (!ValidateSpace(out var message))
        {
            ConfirmDialog.Alert(this, "磁盘空间不足", message, ConfirmDialogKind.Warning);
            return;
        }

        var installDir = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installDir))
        {
            ConfirmDialog.Alert(this, "提示", "请指定安装路径。", ConfirmDialogKind.Info);
            return;
        }

        _manifest.CreateDesktopShortcut = DesktopShortcutBox.IsChecked == true;
        _manifest.CreateStartMenuShortcut = StartMenuShortcutBox.IsChecked == true;

        // 已安装 → 卸载（保留个人文件）后再装
        var existing = InstallProbe.DetectInstalled(installDir, _manifest.ExeName);
        var reinstall = existing.IsInstalled;
        if (reinstall)
        {
            var ver = string.IsNullOrWhiteSpace(existing.Version) ? "" : $"（当前版本 {existing.Version}）";
            var where = existing.InstallDir ?? installDir;
            if (!ConfirmDialog.Ask(
                    this,
                    "已安装",
                    $"{_manifest.ProductName} 已经安装{ver}。\n\n安装位置：\n{where}\n\n将先卸载程序文件（保留个人配置与数据），再安装新版本。是否继续？",
                    "继续安装",
                    "取消",
                    ConfirmDialogKind.Question,
                    defaultYes: false))
                return;

            if (!string.IsNullOrWhiteSpace(existing.InstallDir) && Directory.Exists(existing.InstallDir))
            {
                installDir = existing.InstallDir;
                PathBox.Text = installDir;
            }
        }

        var running = InstallProbe.DetectRunning(installDir, _manifest.ExeName);
        var wasRunning = running.IsRunning;
        if (wasRunning)
        {
            if (!ConfirmDialog.Ask(
                    this,
                    "程序正在运行",
                    $"{_manifest.ProductName} 正在运行，需要先退出后才能继续安装。\n\n是否立即退出并继续安装？",
                    "退出并继续",
                    "取消",
                    ConfirmDialogKind.Warning))
                return;
        }

        // 展开则自动收起
        if (MorePanel.Visibility == Visibility.Visible)
            SetMoreExpanded(false);

        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;
        SetBusy(true);

        try
        {
            IProgress<ProgressReport> progress = new Progress<ProgressReport>(ApplyProgress);

            if (wasRunning)
            {
                await InstallProbe.StopRunningAsync(running, progress, ct).ConfigureAwait(true);
                running = InstallProbe.DetectRunning(installDir, _manifest.ExeName);
                if (running.IsRunning)
                    throw new InvalidOperationException($"无法结束正在运行的 {_manifest.ProductName}，请手动退出后重试。");
                ApplyProgress(ProgressReport.Create(8, "已关闭运行中的程序"));
            }

            // 关闭进程占 0–8%，正式安装映射到 8–100%
            var installProgress = wasRunning
                ? new Progress<ProgressReport>(p =>
                    ApplyProgress(ProgressReport.Create(8 + p.Percent * 0.92, p.Message)))
                : progress;

            var service = new InstallService();
            await service.InstallAsync(_manifest, installDir, _payloadZip, reinstall, installProgress, ct)
                .ConfigureAwait(true);

            ApplyProgress(ProgressReport.Create(100, "安装成功"));
            StatusText.Foreground = (Brush)FindResource("OkBrush");

            if (wasRunning)
            {
                ApplyProgress(ProgressReport.Create(100, "正在重新启动…"));
                InstallService.Launch(installDir, _manifest.ExeName);
                Close();
                return;
            }

            // 原先未在运行（含已安装但未启动）：停在完成页，由勾选决定是否启动
            ShowInstallComplete(installDir);
            return;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消，已回滚到安装前状态";
            StatusText.Foreground = (Brush)FindResource("MutedBrush");
            ConfirmDialog.Alert(this, "已取消",
                "安装已终止。若原先已有版本，已恢复；个人配置未改动。",
                ConfirmDialogKind.Info);
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusText.Text = "安装失败（权限不足）";
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            ConfirmDialog.Alert(this, "安装失败",
                "无法写入安装目录，请使用管理员身份运行安装程序后重试。\n\n" + ex.Message,
                ConfirmDialogKind.Error);
        }
        catch (IOException ex) when (ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                                     || ex.Message.Contains("拒绝", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "安装失败（权限不足）";
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            ConfirmDialog.Alert(this, "安装失败",
                "无法写入安装目录，请使用管理员身份运行安装程序后重试。\n\n" + ex.Message,
                ConfirmDialogKind.Error);
        }
        catch (Exception ex)
        {
            StatusText.Text = "安装失败";
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            var msg = ex.Message;
            if (msg.Contains("denied", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
            {
                msg = "无法写入安装目录，请使用管理员身份运行安装程序后重试。\n\n" + msg;
            }
            ConfirmDialog.Alert(this, "安装失败", msg, ConfirmDialogKind.Error);
        }
        finally
        {
            if (!_finished)
                SetBusy(false);
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private void ShowInstallComplete(string installDir)
    {
        _finished = true;
        _busy = false;
        _finishedInstallDir = installDir;
        if (MorePanel.Visibility == Visibility.Visible)
            SetMoreExpanded(false);
        ApplyBaseWindowSize();
        InstallBody.Visibility = Visibility.Collapsed;
        FinishPanel.Visibility = Visibility.Visible;
        FinishTitle.Text = $"{_manifest.ProductName} 已安装完成";
        FinishPath.Text = installDir;
        LaunchFinishBox.Content = $"启动 {_manifest.ProductName}";
        LaunchFinishBox.IsChecked = true;
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchFinishBox.IsChecked == true && !string.IsNullOrWhiteSpace(_finishedInstallDir))
            InstallService.Launch(_finishedInstallDir, _manifest.ExeName);
        Close();
    }

    private void UpdateInstallEnabled()
    {
        if (_busy) return;
        InstallButton.IsEnabled = AgreeBox.IsChecked == true && _payloadZip != null;
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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        // Hidden 保留占位，避免进度显隐改变窗体高度/按钮位置
        if (_payloadZip == null)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            ProgressBar.IsIndeterminate = false;
            if (busy)
            {
                ProgressBar.Value = 0;
                StatusText.Foreground = (Brush)FindResource("MutedBrush");
                ApplyProgress(ProgressReport.Create(0, "准备安装…"));
            }
        }

        InstallButton.Content = busy ? "取消安装" : "一键安装";
        InstallButton.IsEnabled = busy || (AgreeBox.IsChecked == true && _payloadZip != null);

        AgreeBox.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        MoreButton.IsEnabled = !busy;
        DesktopShortcutBox.IsEnabled = !busy;
        StartMenuShortcutBox.IsEnabled = !busy;
    }

    private void UpdateSpaceInfo()
    {
        var required = Math.Max(_manifest.RequiredBytes, 50L * 1024 * 1024);
        var path = string.IsNullOrWhiteSpace(PathBox.Text)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            : PathBox.Text;

        if (DiskSpace.TryGetFreeBytes(path, out var free))
        {
            var ok = free >= required;
            SpaceText.Text =
                $"需要空间：{DiskSpace.FormatBytes(required)}　　剩余空间：{DiskSpace.FormatBytes(free)}"
                + (ok ? "" : "　　（空间不足）");
            SpaceText.Foreground = ok
                ? (Brush)FindResource("MutedBrush")
                : (Brush)FindResource("DangerBrush");
        }
        else
        {
            SpaceText.Text = $"需要空间：{DiskSpace.FormatBytes(required)}　　（无法读取剩余空间）";
            SpaceText.Foreground = (Brush)FindResource("MutedBrush");
        }
    }

    private bool ValidateSpace(out string message)
    {
        message = "";
        var required = Math.Max(_manifest.RequiredBytes, 50L * 1024 * 1024);
        var path = PathBox.Text.Trim();
        if (!DiskSpace.TryGetFreeBytes(path, out var free))
            return true;
        if (free >= required) return true;
        message =
            $"磁盘剩余空间不足。\n需要：{DiskSpace.FormatBytes(required)}\n剩余：{DiskSpace.FormatBytes(free)}";
        return false;
    }
}
