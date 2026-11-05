using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using FlyScreen.Setup.Models;
using FlyScreen.Setup.Services;
using Microsoft.Win32;

namespace FlyScreen.Setup;

public partial class MainWindow : Window
{
    private readonly string _repoRoot;
    private readonly ObservableCollection<PublishTargetItem> _targets;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        _repoRoot = RepoPaths.FindRepoRoot();
        _targets = new ObservableCollection<PublishTargetItem>(TargetCatalog.CreateDefaults());
        TargetList.ItemsSource = _targets;

        OutputDirBox.Text = RepoPaths.ArtifactsRelease(_repoRoot);
        InstallDirBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "玄鸟");
        LicenseUrlBox.Text = "https://github.com/sclshu3714/FlyScreen";
        CarouselDirBox.Text = Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets", "carousel");

        try
        {
            AssetGenerator.EnsurePublisherAssets(
                Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets"),
                AppendLog);
            AssetGenerator.EnsureInstallerProject(RepoPaths.InstallerDir(_repoRoot), AppendLog);
        }
        catch (Exception ex)
        {
            AppendLog("初始化资源: " + ex.Message);
        }

        AppendLog($"仓库根目录: {_repoRoot}");
        AppendLog("打包固定使用 Release，发布产物将去除 .pdb。");
        ApplyMutexRules();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _targets.Where(x => x.IsEnabled))
            t.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _targets)
            t.IsSelected = false;
    }

    private void PackageMode_Changed(object sender, RoutedEventArgs e) => ApplyMutexRules();

    private void DeployMode_Changed(object sender, RoutedEventArgs e) => ApplyMutexRules();

    /// <summary>
    /// 互斥规则：
    /// - 安装包：SupportsInstaller（Win Setup / Android APK）；其它端禁用
    /// - 独立应用：SupportsStandalone（Android 仅 APK，须选安装包）
    /// - 安装包选项 Expander：仅含 Windows Setup 目标且为安装包模式时可编辑
    /// - mac 端：非 macOS 主机上禁用
    /// </summary>
    private void ApplyMutexRules()
    {
        if (ModeInstaller == null || DeployFramework == null) return;

        var installerMode = ModeInstaller.IsChecked == true;
        var frameworkMode = DeployFramework.IsChecked == true;

        InstallerHint.Visibility = installerMode ? Visibility.Visible : Visibility.Collapsed;

        foreach (var t in _targets)
        {
            string? reason = null;
            var enabled = true;

            if (t.RequiresMac && !OperatingSystem.IsMacOS())
            {
                enabled = false;
                reason = "须在 macOS 上构建";
            }
            else if (installerMode && !t.SupportsInstaller)
            {
                enabled = false;
                reason = "当前为安装包模式，此端请改用「独立应用」";
            }
            else if (!installerMode && !t.SupportsStandalone)
            {
                enabled = false;
                reason = "此端产出为安装包（APK），请改选「安装包」";
            }
            else if (frameworkMode && !t.SupportsSelfContained)
            {
                reason = "此端发布不受 Self-contained/依赖框架影响";
            }

            t.IsEnabled = enabled;
            t.DisabledReason = reason ?? t.Rid;
            if (!enabled)
                t.IsSelected = false;
        }

        // Windows Setup 选项：仅安装包且仍有可用的 Win Setup 端时启用
        var anyWindowsSetup = _targets.Any(t => t.IsEnabled && t.UsesWindowsSetup);
        InstallerOptionsExpander.IsEnabled = installerMode && anyWindowsSetup;
        if (!installerMode || !anyWindowsSetup)
            InstallerOptionsExpander.IsExpanded = false;

        DeployHint.Text = frameworkMode
            ? "依赖框架：产物更小，目标机器需已安装对应 .NET 运行时。Android 不受此选项影响。"
            : "独立部署：自带运行时，体积更大，无需预装 .NET。";

        var enabledCount = _targets.Count(t => t.IsEnabled);
        var selectedCount = _targets.Count(t => t.IsSelected);
        TargetMutexHint.Text = installerMode
            ? $"安装包可选 Win Setup / Android APK，共 {enabledCount} 个端；已选 {selectedCount} 个。"
            : $"独立应用可选 {enabledCount} 个端；已选 {selectedCount} 个。";
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择输出目录" };
        if (!string.IsNullOrWhiteSpace(OutputDirBox.Text) && Directory.Exists(OutputDirBox.Text))
            dlg.InitialDirectory = OutputDirBox.Text;
        if (dlg.ShowDialog(this) == true)
            OutputDirBox.Text = dlg.FolderName;
    }

    private void BrowseCarousel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择轮播图目录（PNG）" };
        if (!string.IsNullOrWhiteSpace(CarouselDirBox.Text) && Directory.Exists(CarouselDirBox.Text))
            dlg.InitialDirectory = CarouselDirBox.Text;
        if (dlg.ShowDialog(this) == true)
            CarouselDirBox.Text = dlg.FolderName;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        ApplyMutexRules();
        var selected = _targets.Where(t => t.IsSelected && t.IsEnabled).ToList();
        if (selected.Count == 0)
        {
            MessageDialog.Alert(this, "提示", "请至少勾选一个可用的打包目标。", MessageDialogKind.Info);
            return;
        }

        var options = BuildOptions();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        ApplyProgress(PublishProgress.Create(0, "打包中（Release）…"));

        try
        {
            var service = new PublishService(_repoRoot);
            var progress = new Progress<PublishProgress>(p =>
            {
                Dispatcher.Invoke(() => ApplyProgress(p));
            });
            await service.PublishAsync(selected, options, msg =>
            {
                Dispatcher.Invoke(() => AppendLog(msg));
            }, progress, _cts.Token).ConfigureAwait(true);

            ApplyProgress(PublishProgress.Create(100, "全部任务完成"));
            MessageDialog.CompleteWithFolder(
                this,
                "完成",
                "打包完成。可打开输出目录查看产物。",
                options.OutputRoot);
        }
        catch (OperationCanceledException)
        {
            AppendLog("已取消。");
            StatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            AppendLog("错误: " + ex.Message);
            StatusText.Text = "打包失败";
            MessageDialog.Alert(this, "打包失败", ex.Message, MessageDialogKind.Error);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ApplyProgress(PublishProgress report)
    {
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Value = report.Percent;
        var pct = (int)Math.Round(report.Percent);
        StatusText.Text = string.IsNullOrWhiteSpace(report.Message)
            ? $"{pct}%"
            : $"{report.Message}  {pct}%";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private PublishOptions BuildOptions() => new()
    {
        PackageMode = ModeInstaller.IsChecked == true ? PackageMode.Installer : PackageMode.Standalone,
        DeploymentMode = DeploySelfContained.IsChecked == true
            ? DeploymentMode.SelfContained
            : DeploymentMode.FrameworkDependent,
        // 始终 Release，禁止打 Debug
        Configuration = "Release",
        OutputRoot = string.IsNullOrWhiteSpace(OutputDirBox.Text)
            ? RepoPaths.ArtifactsRelease(_repoRoot)
            : OutputDirBox.Text.Trim(),
        ProductName = string.IsNullOrWhiteSpace(ProductNameBox.Text) ? "玄鸟" : ProductNameBox.Text.Trim(),
        DefaultInstallDir = string.IsNullOrWhiteSpace(InstallDirBox.Text)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "玄鸟")
            : InstallDirBox.Text.Trim(),
        LicenseUrl = LicenseUrlBox.Text.Trim(),
        CarouselDir = CarouselDirBox.Text.Trim(),
        LicenseFile = Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets", "license.txt"),
        CreateDesktopShortcut = DesktopShortcutBox.IsChecked == true,
        CreateStartMenuShortcut = StartMenuShortcutBox.IsChecked == true,
        LaunchAfterInstall = LaunchAfterBox.IsChecked == true,
        IncludeVddOffline = IncludeVddOfflineBox.IsChecked == true
    };

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        TargetList.IsEnabled = !busy;
        ModeStandalone.IsEnabled = !busy;
        ModeInstaller.IsEnabled = !busy;
        DeploySelfContained.IsEnabled = !busy;
        DeployFramework.IsEnabled = !busy;
        IncludeVddOfflineBox.IsEnabled = !busy;
        OutputDirBox.IsEnabled = !busy;
        InstallerOptionsExpander.IsEnabled = !busy && ModeInstaller.IsChecked == true;

        // 进度条贴窗底：默认隐藏，仅打包过程显示
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
            ProgressBar.Value = 0;
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (LogBox.Text.Length > 0) LogBox.AppendText(Environment.NewLine);
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
    }
}
