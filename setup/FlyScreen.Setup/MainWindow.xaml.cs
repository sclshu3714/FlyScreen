using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlyScreen.Setup.Models;
using FlyScreen.Setup.Services;
using Microsoft.Win32;

namespace FlyScreen.Setup;

public partial class MainWindow : Window
{
    private readonly int _maxConcurrentJobs;
    private readonly PublishConcurrency.Decision _concurrency;

    private readonly string _repoRoot;
    private readonly ObservableCollection<PublishTargetItem> _targets;
    private readonly ObservableCollection<PublishJobItem> _jobs = [];
    private readonly object _schedulerLock = new();
    private readonly HashSet<Guid> _runningIds = [];

    private bool _suppressCascade;
    private bool _suppressTargetRadio;
    private PublishTargetItem? _cascadeTarget;
    private PublishJobItem? _logFocusJob;
    private readonly ObservableCollection<SetupLogEntry> _systemLogs = [];

    public MainWindow()
    {
        InitializeComponent();
        _concurrency = PublishConcurrency.Resolve();
        _maxConcurrentJobs = _concurrency.MaxJobs;
        _repoRoot = RepoPaths.FindRepoRoot();
        _targets = new ObservableCollection<PublishTargetItem>(TargetCatalog.CreateDefaults());
        foreach (var t in _targets)
        {
            t.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PublishTargetItem.IsSelected)
                    or nameof(PublishTargetItem.IsEnabled))
                {
                    ApplyVddOfflineState();
                    RefreshCascadeFromSelection();
                    UpdateQueueEmptyHint();
                }
            };
        }

        TargetList.ItemsSource = _targets;
        JobList.ItemsSource = _jobs;
        LogList.ItemsSource = _systemLogs;
        _jobs.CollectionChanged += Jobs_CollectionChanged;

        OutputDirBox.Text = RepoPaths.ArtifactsRelease(_repoRoot);
        InstallDirBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "XuanNiao");
        LicenseUrlBox.Text = "https://github.com/sclshu3714/FlyScreen";
        CarouselDirBox.Text = Path.Combine(_repoRoot, "src", "FlyScreen.Assets", "Installer");

        try
        {
            AssetGenerator.EnsurePublisherAssets(
                Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets"),
                AppendSystemLog);
            AssetGenerator.EnsureInstallerProject(RepoPaths.InstallerDir(_repoRoot), AppendSystemLog);
        }
        catch (Exception ex)
        {
            AppendSystemLog("初始化资源: " + ex.Message);
        }

        AppendSystemLog($"仓库根目录: {_repoRoot}");
        AppendSystemLog("打包固定使用 Release，发布产物将去除 .pdb。");
        AppendSystemLog(_concurrency.Summary);
        ApplyMutexRules();
        RefreshCascadeFromSelection();
        UpdateQueueEmptyHint();
        RefreshStatusBar();
    }

    private void Jobs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateQueueEmptyHint();

    private void UpdateQueueEmptyHint()
    {
        if (QueueEmptyHint == null) return;
        QueueEmptyHint.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TargetRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressTargetRadio || sender is not RadioButton { DataContext: PublishTargetItem selected })
            return;

        _suppressTargetRadio = true;
        try
        {
            foreach (var t in _targets)
                t.IsSelected = ReferenceEquals(t, selected);
        }
        finally
        {
            _suppressTargetRadio = false;
        }

        ApplyVddOfflineState();
        RefreshCascadeFromSelection();
    }

    private void PackageMode_Changed(object sender, RoutedEventArgs e) => ApplyMutexRules();

    private void DeployMode_Changed(object sender, RoutedEventArgs e) => ApplyMutexRules();

    private void ApplyMutexRules()
    {
        if (ModeInstaller == null || DeployFramework == null || _targets == null) return;

        var installerMode = ModeInstaller.IsChecked == true;
        var frameworkMode = DeployFramework.IsChecked == true;

        InstallerHint.Visibility = installerMode ? Visibility.Visible : Visibility.Collapsed;

        PublishTargetItem? keep = null;
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
            else if (t.IsSelected)
                keep = t;
        }

        if (keep == null)
        {
            var first = _targets.FirstOrDefault(t => t.IsEnabled);
            if (first != null)
                first.IsSelected = true;
        }

        var anyWindowsSetup = _targets.Any(t => t.IsEnabled && t.UsesWindowsSetup);
        InstallerOptionsExpander.IsEnabled = installerMode && anyWindowsSetup;
        if (!installerMode || !anyWindowsSetup)
            InstallerOptionsExpander.IsExpanded = false;

        DeployHint.Text = frameworkMode
            ? "依赖框架：产物更小，目标机器需已安装对应 .NET 运行时。Android 不受此选项影响。"
            : "独立部署：自带运行时，体积更大，无需预装 .NET。";

        var enabledCount = _targets.Count(t => t.IsEnabled);
        var selected = _targets.FirstOrDefault(t => t.IsSelected && t.IsEnabled);
        TargetMutexHint.Text = installerMode
            ? $"安装包可选 Win Setup / Android APK / Linux tar.gz，共 {enabledCount} 个端；当前：{selected?.DisplayName ?? "未选"}。"
            : $"独立应用可选 {enabledCount} 个端；当前：{selected?.DisplayName ?? "未选"}。";

        ApplyVddOfflineState();
        RefreshCascadeFromSelection();
    }

    private void RefreshCascadeFromSelection()
    {
        if (FrameworkBox == null || RuntimeBox == null || _targets == null) return;

        var active = _targets.FirstOrDefault(t => t.IsSelected && t.IsEnabled)
                     ?? _targets.FirstOrDefault(t => t.IsEnabled)
                     ?? _targets.FirstOrDefault();
        _cascadeTarget = active;

        _suppressCascade = true;
        try
        {
            FrameworkBox.Items.Clear();
            RuntimeBox.Items.Clear();

            if (active == null || active.FrameworkChoices.Count == 0)
            {
                FrameworkBox.IsEnabled = false;
                RuntimeBox.IsEnabled = false;
                FrameworkHint.Text = "当前无可编辑的打包目标。";
                RuntimeHint.Text = "";
                return;
            }

            foreach (var fw in active.FrameworkChoices.Select(c => c.Framework))
                FrameworkBox.Items.Add(fw);

            var selectedFw = active.SelectedFramework
                             ?? active.FrameworkChoices[0].Framework;
            FrameworkBox.SelectedItem = FrameworkBox.Items
                .Cast<string>()
                .FirstOrDefault(x => string.Equals(x, selectedFw, StringComparison.OrdinalIgnoreCase))
                ?? FrameworkBox.Items[0];

            FillRuntimesForFramework(active, FrameworkBox.SelectedItem as string);

            FrameworkBox.IsEnabled = active.IsEnabled;
            RuntimeBox.IsEnabled = active.IsEnabled && RuntimeBox.Items.Count > 0;

            FrameworkHint.Text = $"当前目标：{active.DisplayName}。可选框架随打包目标变化。";
            RuntimeHint.Text = "可选运行时随目标框架变化。";
        }
        finally
        {
            _suppressCascade = false;
        }
    }

    private void FillRuntimesForFramework(PublishTargetItem target, string? framework)
    {
        RuntimeBox.Items.Clear();
        var choice = target.FrameworkChoices.FirstOrDefault(c =>
            string.Equals(c.Framework, framework, StringComparison.OrdinalIgnoreCase));
        if (choice == null) return;

        foreach (var rt in choice.Runtimes)
            RuntimeBox.Items.Add(rt);

        var preferred = target.SelectedRuntime;
        RuntimeBox.SelectedItem = RuntimeBox.Items
                                      .Cast<string>()
                                      .FirstOrDefault(x =>
                                          string.Equals(x, preferred, StringComparison.OrdinalIgnoreCase))
                                  ?? (RuntimeBox.Items.Count > 0 ? RuntimeBox.Items[0] : null);
    }

    private void FrameworkBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCascade || _cascadeTarget == null) return;
        var fw = FrameworkBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(fw)) return;

        _suppressCascade = true;
        try
        {
            _cascadeTarget.SelectedFramework = fw;
            FillRuntimesForFramework(_cascadeTarget, fw);
            var rt = RuntimeBox.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(rt))
                _cascadeTarget.SelectedRuntime = rt;
            _cascadeTarget.ApplySelectedFrameworkRuntime();
        }
        finally
        {
            _suppressCascade = false;
        }
    }

    private void RuntimeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCascade || _cascadeTarget == null) return;
        var rt = RuntimeBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(rt)) return;

        _cascadeTarget.SelectedRuntime = rt;
        if (FrameworkBox.SelectedItem is string fw)
            _cascadeTarget.SelectedFramework = fw;
        _cascadeTarget.ApplySelectedFrameworkRuntime();
    }

    private void ApplyVddOfflineState()
    {
        if (IncludeVddOfflineBox == null || VddOfflineHint == null || _targets == null) return;

        var windowsSelected = _targets.Any(t => t.IsEnabled && t.IsSelected && t.SupportsVddOffline);
        IncludeVddOfflineBox.IsEnabled = windowsSelected;
        if (!windowsSelected)
            IncludeVddOfflineBox.IsChecked = false;

        VddOfflineHint.Text = windowsSelected
            ? "仅打进当前选中的 Windows 电脑端（Windows 桌面 / MAUI Win）。不勾选则体积更小，使用时通过 winget/在线安装。"
            : "虚拟显示离线包仅 Windows 电脑端支持。当前未选 Windows 桌面或 MAUI Win，此选项不可用。";
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择输出目录" };
        if (!string.IsNullOrWhiteSpace(OutputDirBox.Text) && Directory.Exists(OutputDirBox.Text))
            dlg.InitialDirectory = OutputDirBox.Text;
        if (dlg.ShowDialog(this) == true)
            OutputDirBox.Text = dlg.FolderName;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        // 优先打开最近一次入队任务的输出目录，否则用当前配置的输出目录
        var path = _jobs.LastOrDefault()?.Options.OutputRoot;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = string.IsNullOrWhiteSpace(OutputDirBox.Text)
                ? RepoPaths.ArtifactsRelease(_repoRoot)
                : OutputDirBox.Text.Trim();
        }

        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Alert(this, "提示", "无法打开输出目录：\n" + ex.Message, MessageDialogKind.Info);
        }
    }

    private void BrowseCarousel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择轮播图目录（PNG）" };
        if (!string.IsNullOrWhiteSpace(CarouselDirBox.Text) && Directory.Exists(CarouselDirBox.Text))
            dlg.InitialDirectory = CarouselDirBox.Text;
        if (dlg.ShowDialog(this) == true)
            CarouselDirBox.Text = dlg.FolderName;
    }

    private void ClearLog_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 阻止点击「清空」时触发展开/折叠
        e.Handled = true;
        ClearLog_Click(sender, e);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logFocusJob != null)
            _logFocusJob.ClearLogs();
        else
            _systemLogs.Clear();
    }

    private void LogExpander_Expanded(object sender, RoutedEventArgs e)
        => Dispatcher.BeginInvoke(ScrollLogToEnd, System.Windows.Threading.DispatcherPriority.Loaded);

    private void LogExpander_Collapsed(object sender, RoutedEventArgs e) { }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        ApplyMutexRules();
        var selected = _targets.FirstOrDefault(t => t.IsSelected && t.IsEnabled);
        if (selected == null)
        {
            MessageDialog.Alert(this, "提示", "请选择一个可用的打包目标。", MessageDialogKind.Info);
            return;
        }

        selected.ApplySelectedFrameworkRuntime();
        var snapshot = selected.CloneForPublish();
        var options = BuildOptions().Clone();

        var job = new PublishJobItem
        {
            Title = snapshot.DisplayName,
            Subtitle = $"{snapshot.Rid} · {snapshot.SelectedFramework ?? snapshot.Framework}",
            Target = snapshot,
            Options = options,
            Message = "等待执行",
            Status = PublishJobStatus.Waiting,
            Percent = 0
        };
        job.AppendLog($"已加入队列：{job.Title} ({job.Subtitle})", SetupLogLevel.Info);

        _jobs.Add(job);
        AppendSystemLog($"任务入队：{job.Title}（{job.Subtitle}）");
        RefreshStatusBar();
        TryScheduleJobs();
    }

    private void TryScheduleJobs()
    {
        List<PublishJobItem> toStart;
        lock (_schedulerLock)
        {
            var slots = _maxConcurrentJobs - _runningIds.Count;
            if (slots <= 0) return;

            // 同一 csproj（如 maui-win / maui-android）不可并行：WinAppSDK MsixContent、Android obj 都落在工程目录。
            var busyProjects = _jobs
                .Where(j => j.Status == PublishJobStatus.Running || _runningIds.Contains(j.Id))
                .Select(j => NormalizeProjectKey(j.Target.ProjectPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            toStart = [];
            foreach (var job in _jobs.Where(j => j.Status == PublishJobStatus.Waiting))
            {
                if (slots <= 0) break;
                var key = NormalizeProjectKey(job.Target.ProjectPath);
                if (!busyProjects.Add(key))
                {
                    if (string.IsNullOrWhiteSpace(job.Message) || job.Message == "等待执行")
                        job.Message = "等待同工程任务结束…";
                    continue;
                }

                job.Status = PublishJobStatus.Running;
                job.Message = "准备中…";
                job.Percent = 0;
                _runningIds.Add(job.Id);
                toStart.Add(job);
                slots--;
            }
        }

        foreach (var job in toStart)
            _ = RunJobAsync(job);

        RefreshStatusBar();
    }

    private static string NormalizeProjectKey(string? projectPath)
        => (projectPath ?? "").Replace('/', '\\').Trim().ToLowerInvariant();

    private async Task RunJobAsync(PublishJobItem job)
    {
        var service = new PublishService(_repoRoot);
        try
        {
            var progress = new Progress<PublishProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    job.Percent = p.Percent;
                    job.Message = p.Message;
                    RefreshStatusBar();
                });
            });

            await service.PublishAsync(
                [job.Target],
                job.Options,
                msg =>
                {
                    Dispatcher.Invoke(() => AppendJobLog(job, msg));
                },
                progress,
                job.Cts.Token,
                jobId: job.Id.ToString("N")).ConfigureAwait(true);

            job.Percent = 100;
            job.Message = "全部任务完成";
            job.Status = PublishJobStatus.Completed;
            AppendJobLog(job, "打包完成。", SetupLogLevel.Info);
        }
        catch (OperationCanceledException)
        {
            job.Status = PublishJobStatus.Cancelled;
            job.Message = "已取消";
            AppendJobLog(job, "任务已取消。", SetupLogLevel.Warn);
        }
        catch (Exception ex)
        {
            job.Status = PublishJobStatus.Failed;
            job.Message = ex.Message;
            AppendJobLog(job, "错误: " + ex.Message, SetupLogLevel.Error);
        }
        finally
        {
            lock (_schedulerLock)
                _runningIds.Remove(job.Id);

            try { job.Cts.Dispose(); }
            catch { /* ignore */ }

            RefreshStatusBar();
            TryScheduleJobs();
        }
    }

    private void JobDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PublishJobItem job }) return;

        var wasRunning = job.Status == PublishJobStatus.Running;
        if (wasRunning || job.Status == PublishJobStatus.Waiting)
        {
            try { job.Cts.Cancel(); }
            catch { /* ignore */ }
            if (wasRunning)
                AppendJobLog(job, "正在取消…");
            job.Status = PublishJobStatus.Cancelled;
            job.Message = "已取消";
        }

        if (_logFocusJob?.Id == job.Id)
        {
            _logFocusJob.IsLogFocused = false;
            _logFocusJob = null;
            ShowSystemLogs();
        }

        _jobs.Remove(job);

        // 运行中的 CTS 交由 RunJobAsync.finally 释放；等待中的此处释放
        if (!wasRunning)
        {
            try { job.Cts.Dispose(); }
            catch { /* ignore */ }
            lock (_schedulerLock)
                _runningIds.Remove(job.Id);
        }

        RefreshStatusBar();
        TryScheduleJobs();
    }

    private void JobLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PublishJobItem job }) return;
        ShowJobLog(job);
    }

    private void JobRetry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PublishJobItem job }) return;
        if (!job.CanRetry) return;

        job.PrepareRetry();
        AppendSystemLog($"重新打包：{job.Title}");
        if (_logFocusJob?.Id == job.Id)
            ScrollLogToEnd();
        RefreshStatusBar();
        TryScheduleJobs();
    }

    private void ShowJobLog(PublishJobItem job)
    {
        if (_logFocusJob != null)
            _logFocusJob.IsLogFocused = false;

        _logFocusJob = job;
        job.IsLogFocused = true;
        LogTitle.Text = $"输出日志 · {job.Title}";
        LogList.ItemsSource = job.Logs;
        LogExpander.IsExpanded = true;
        ScrollLogToEnd();
    }

    private void ShowSystemLogs()
    {
        LogTitle.Text = "输出日志";
        LogList.ItemsSource = _systemLogs;
    }

    private void AppendJobLog(PublishJobItem job, string message, SetupLogLevel? level = null)
    {
        job.AppendLog(message, level);
        if (_logFocusJob?.Id == job.Id)
            ScrollLogToEnd();
    }

    private void AppendSystemLog(string message)
    {
        _systemLogs.Add(SetupLogEntry.FromMessage(message));
        if (_logFocusJob == null)
            ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count == 0) return;
        var last = LogList.Items[^1];
        LogList.UpdateLayout();
        LogList.ScrollIntoView(last);
        // 再滚一点，避免末行被圆角/边距裁切
        if (FindScrollViewer(LogList) is { } sv)
            sv.ScrollToEnd();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    private void RefreshStatusBar()
    {
        if (StatusText == null) return;
        var running = _jobs.Count(j => j.Status == PublishJobStatus.Running);
        var waiting = _jobs.Count(j => j.Status == PublishJobStatus.Waiting);
        var done = _jobs.Count(j => j.Status is PublishJobStatus.Completed or PublishJobStatus.Failed or PublishJobStatus.Cancelled);
        StatusText.Text = $"就绪 · Release · 运行 {running}/{_maxConcurrentJobs} · 等待 {waiting} · 结束 {done}";
    }

    private PublishOptions BuildOptions() => new()
    {
        PackageMode = ModeInstaller.IsChecked == true ? PackageMode.Installer : PackageMode.Standalone,
        DeploymentMode = DeploySelfContained.IsChecked == true
            ? DeploymentMode.SelfContained
            : DeploymentMode.FrameworkDependent,
        Configuration = "Release",
        OutputRoot = string.IsNullOrWhiteSpace(OutputDirBox.Text)
            ? RepoPaths.ArtifactsRelease(_repoRoot)
            : OutputDirBox.Text.Trim(),
        ProductName = string.IsNullOrWhiteSpace(ProductNameBox.Text) ? "玄鸟" : ProductNameBox.Text.Trim(),
        DefaultInstallDir = string.IsNullOrWhiteSpace(InstallDirBox.Text)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "XuanNiao")
            : InstallDirBox.Text.Trim(),
        LicenseUrl = LicenseUrlBox.Text.Trim(),
        CarouselDir = CarouselDirBox.Text.Trim(),
        LicenseFile = Path.Combine(RepoPaths.PublisherDir(_repoRoot), "Assets", "license.txt"),
        CreateDesktopShortcut = DesktopShortcutBox.IsChecked == true,
        CreateStartMenuShortcut = StartMenuShortcutBox.IsChecked == true,
        IncludeVddOffline = IncludeVddOfflineBox.IsChecked == true
                            && _targets.Any(t => t.IsSelected && t.IsEnabled && t.SupportsVddOffline)
    };
}
