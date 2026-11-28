using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly ObservableCollection<FlyToolSelectItem> _flyTools;
    private readonly ObservableCollection<PublishJobItem> _jobs = [];
    private readonly object _schedulerLock = new();
    private readonly HashSet<Guid> _runningIds = [];

    private bool _suppressCascade;
    private bool _suppressTargetRadio;
    private PublishTargetItem? _cascadeTarget;
    private PublishJobItem? _logFocusJob;
    private readonly ObservableCollection<SetupLogEntry> _systemLogs = [];
    private bool _logFullscreen;
    private GridLength _savedQueueHeight = new(1, GridUnitType.Star);
    private GridLength _savedLogHeight = new(1.15, GridUnitType.Star);
    private double _savedQueueMinHeight = 100;
    private double _savedLogMinHeight = 140;
    private bool _suppressDockerSettingsSave;

    public MainWindow()
    {
        InitializeComponent();
        _concurrency = PublishConcurrency.Resolve();
        _maxConcurrentJobs = _concurrency.MaxJobs;
        _repoRoot = RepoPaths.FindRepoRoot();
        _targets = new ObservableCollection<PublishTargetItem>(TargetCatalog.CreateDefaults());
        var flyTools = FlyToolCatalog.Refresh(_repoRoot);
        _flyTools = new ObservableCollection<FlyToolSelectItem>(
            flyTools.Select(t => new FlyToolSelectItem
            {
                Id = t.Id,
                DisplayName = t.DisplayName,
                Platforms = t.Platforms
            }));
        foreach (var t in _targets)
        {
            t.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PublishTargetItem.IsSelected)
                    or nameof(PublishTargetItem.IsEnabled))
                {
                    ApplyVddOfflineState();
                    ApplyFlyToolsState();
                    RefreshCascadeFromSelection();
                    UpdateQueueEmptyHint();
                }
            };
        }

        TargetList.ItemsSource = _targets;
        FlyToolList.ItemsSource = _flyTools;
        JobList.ItemsSource = _jobs;
        LogList.ItemsSource = _systemLogs;
        _jobs.CollectionChanged += Jobs_CollectionChanged;

        OutputDirBox.Text = RepoPaths.ArtifactsRelease(_repoRoot);
        VersionBox.Text = VersionResolver.ReadCurrent(_repoRoot);
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

        LoadDockerUiSettings();
        Closing += (_, _) => SaveDockerUiSettings();

        AppendSystemLog($"仓库根目录: {_repoRoot}");
        AppendSystemLog($"当前版本: {VersionBox.Text}");
        AppendSystemLog("打包固定使用 Release，发布产物将去除 .pdb。");
        AppendSystemLog(_concurrency.Summary);
        ApplyMutexRules();
        ApplyFlyToolsState();
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
        ApplyFlyToolsState();
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

        var selected = _targets.FirstOrDefault(t => t.IsSelected && t.IsEnabled);
        if (selected?.IsFlyTools == true)
        {
            DeployHint.Text = "插件工具组包不受 Self-contained/依赖框架影响，始终 Release 产出 .flytool。";
        }
        else if (selected?.IsRelayDocker == true)
        {
            DeployHint.Text = "Docker 镜像打包不受部署模式影响；本机或 SSH 远程构建，默认 -Mirror 并导出 .tar。";
        }
        else if (selected?.IsRelay == true)
        {
            DeployHint.Text = frameworkMode
                ? "中继服务依赖框架：产物更小，目标机需 .NET 10 运行时。运行时选 docker 可改打镜像。"
                : "中继服务独立部署：自带运行时。运行时选 docker 可改为打包 Docker 镜像。";
        }
        else
        {
            DeployHint.Text = frameworkMode
                ? "依赖框架：产物更小，目标机器需已安装对应 .NET 运行时。Android 不受此选项影响。"
                : "独立部署：自带运行时，体积更大，无需预装 .NET。";
        }

        var enabledCount = _targets.Count(t => t.IsEnabled);
        TargetMutexHint.Text = DescribeTargetHint(installerMode, enabledCount, selected);

        ApplyVddOfflineState();
        ApplyFlyToolsState();
        RefreshCascadeFromSelection();
        RefreshDockerHostPanel();
    }

    private void IncludeFlyTools_Changed(object sender, RoutedEventArgs e) => ApplyFlyToolsState();

    private static string DescribeTargetHint(bool installerMode, int enabledCount, PublishTargetItem? selected)
    {
        var name = selected?.DisplayName ?? "未选";
        if (selected?.IsFlyTools == true)
            return $"插件工具：输出 FlyScreen-{{ver}}-flytools.zip（及目录副本）；当前：{name}。";
        if (selected?.IsRelayDocker == true)
            return $"中继 Docker：本机或 SSH 远程构建，导出 FlyScreen-{{ver}}-relay-docker.tar；当前：{name}。";
        if (selected?.IsRelay == true)
            return installerMode
                ? $"中继服务安装包：zip 目录产物；运行时选 docker 可打镜像。当前：{name}。"
                : $"中继服务独立应用：单文件或 zip；运行时选 docker 可打镜像。当前：{name}。";
        return installerMode
            ? $"安装包可选 Win Setup / Android APK / Linux tar.gz / 插件 / 中继，共 {enabledCount} 项；当前：{name}。"
            : $"独立应用可选 {enabledCount} 项；当前：{name}。";
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
            RuntimeHint.Text = active.IsRelay
                ? "中继可选本机 RID，或选 docker（本机 Docker Desktop / SSH 远程构建）。"
                : active.IsFlyTools
                    ? "插件工具无平台 RID，bundled 表示组全部内置 .flytool。"
                    : "可选运行时随目标框架变化。";
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
        // Relay 选 docker 时部署提示 / Docker 面板需刷新
        if (_cascadeTarget.IsRelay)
            ApplyMutexRules();
        else
            RefreshDockerHostPanel();
    }

    private void RefreshDockerHostPanel()
    {
        if (DockerHostPanel == null) return;
        var selected = _targets?.FirstOrDefault(t => t.IsSelected && t.IsEnabled) ?? _cascadeTarget;
        var docker = selected?.IsRelayDocker == true;
        DockerHostPanel.Visibility = docker ? Visibility.Visible : Visibility.Collapsed;
        if (!docker) return;

        TrySplitHostPortFromIpBox();
        var local = IsLocalDockerHostUi(DockerHostBox?.Text);
        if (DockerRemoteAuthPanel != null)
            DockerRemoteAuthPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        if (DockerPortBox != null)
            DockerPortBox.IsEnabled = !local;
        if (DockerHostHint != null)
        {
            DockerHostHint.Text = local
                ? "本机模式：请先启动 Docker Desktop；Setup 会自动补齐常见 Docker PATH。"
                : GetDockerAuthMode() switch
                {
                    "key" => "SSH 密钥：远端 build/save 后拉回（无需本机 docker）。自动检测：能直连 docker 则不加 sudo，否则用免密 sudo。",
                    "password" => "SSH 密码：远端 build/save 后拉回。自动检测 docker 权限；普通用户无权限时再用同一密码 sudo，管理员/docker 组用户不加 sudo。",
                    _ => "TCP 无认证：DOCKER_HOST=tcp://IP:端口（如 2375）。本机需 docker 客户端。"
                };
        }

        RefreshDockerAuthRows();
    }

    private void RefreshDockerAuthRows()
    {
        if (DockerKeyBox == null || DockerPasswordBox == null) return;
        var mode = GetDockerAuthMode();
        var key = mode == "key";
        var pwd = mode == "password";
        var ssh = key || pwd;

        if (DockerCredentialPanel != null)
            DockerCredentialPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;

        DockerKeyBox.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        DockerPasswordBox.Visibility = pwd ? Visibility.Visible : Visibility.Collapsed;
        if (DockerSecretLabel != null)
            DockerSecretLabel.Text = key ? "密钥" : "密码";
        if (DockerUserBox != null)
            DockerUserBox.IsEnabled = ssh;
    }

    private string GetDockerAuthMode()
    {
        if (DockerAuthKeyRadio?.IsChecked == true) return "key";
        if (DockerAuthPasswordRadio?.IsChecked == true) return "password";
        return "tcp";
    }

    private static bool IsLocalDockerHostUi(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var h = host.Trim();
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || h.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private void DockerHostFields_Changed(object sender, TextChangedEventArgs e)
    {
        RefreshDockerHostPanel();
        SaveDockerUiSettings();
    }

    private void DockerUserBox_TextChanged(object sender, TextChangedEventArgs e)
        => SaveDockerUiSettings();

    private void DockerAuthMode_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: not true }) return;

        // 切换模式时给常用默认端口提示性纠正
        if (DockerPortBox != null && int.TryParse(DockerPortBox.Text.Trim(), out var port))
        {
            var mode = GetDockerAuthMode();
            if (mode == "tcp" && port == 22)
                DockerPortBox.Text = "2375";
            else if (mode is "key" or "password" && port is 2375 or 2376)
                DockerPortBox.Text = "22";
        }

        RefreshDockerAuthRows();
        RefreshDockerHostPanel();
        SaveDockerUiSettings();
    }

    private void LoadDockerUiSettings()
    {
        _suppressDockerSettingsSave = true;
        try
        {
            var s = SetupUiSettings.Load();
            if (DockerHostBox != null && !string.IsNullOrWhiteSpace(s.DockerHost))
                DockerHostBox.Text = s.DockerHost.Trim();
            if (DockerPortBox != null && !string.IsNullOrWhiteSpace(s.DockerPort))
                DockerPortBox.Text = s.DockerPort.Trim();
            if (DockerUserBox != null && !string.IsNullOrWhiteSpace(s.DockerUser))
                DockerUserBox.Text = s.DockerUser.Trim();

            switch ((s.DockerAuthMode ?? "tcp").Trim().ToLowerInvariant())
            {
                case "key":
                    if (DockerAuthKeyRadio != null) DockerAuthKeyRadio.IsChecked = true;
                    break;
                case "password":
                    if (DockerAuthPasswordRadio != null) DockerAuthPasswordRadio.IsChecked = true;
                    break;
                default:
                    if (DockerAuthTcpRadio != null) DockerAuthTcpRadio.IsChecked = true;
                    break;
            }

            RefreshDockerAuthRows();
            RefreshDockerHostPanel();
        }
        catch
        {
            // 忽略损坏的本地偏好
        }
        finally
        {
            _suppressDockerSettingsSave = false;
        }
    }

    private void SaveDockerUiSettings()
    {
        if (_suppressDockerSettingsSave) return;
        try
        {
            var s = new SetupUiSettings
            {
                DockerHost = DockerHostBox?.Text?.Trim() ?? "",
                DockerPort = DockerPortBox?.Text?.Trim() ?? "2375",
                DockerAuthMode = GetDockerAuthMode(),
                DockerUser = DockerUserBox?.Text?.Trim() ?? ""
            };
            s.Save();
        }
        catch
        {
            // 写盘失败不影响打包
        }
    }

    /// <summary>支持在 IP 框粘贴 host:port。</summary>
    private void TrySplitHostPortFromIpBox()
    {
        if (DockerHostBox == null || DockerPortBox == null) return;
        var raw = DockerHostBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw) || !raw.Contains(':')) return;
        // 避免 IPv6
        if (raw.Count(c => c == ':') != 1) return;
        var parts = raw.Split(':', 2);
        if (parts.Length != 2) return;
        if (!int.TryParse(parts[1], out var port) || port is < 1 or > 65535) return;
        if (string.Equals(DockerHostBox.Text?.Trim(), parts[0], StringComparison.Ordinal)
            && string.Equals(DockerPortBox.Text?.Trim(), parts[1], StringComparison.Ordinal))
            return;
        DockerHostBox.Text = parts[0];
        DockerPortBox.Text = parts[1];
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

    private void ApplyFlyToolsState()
    {
        if (FlyToolsPanel == null || IncludeFlyToolsBox == null || FlyToolsHint == null || _flyTools == null)
            return;

        var selected = _targets?.FirstOrDefault(t => t.IsSelected && t.IsEnabled);
        var isFlyToolsTarget = selected?.IsFlyTools == true;
        var canBundle = selected?.SupportsFlyToolsBundle == true;
        var platform = FlyToolCatalog.ResolvePlatform(selected?.Id);

        FlyToolsPanel.Visibility = isFlyToolsTarget || canBundle
            ? Visibility.Visible
            : Visibility.Collapsed;

        IncludeFlyToolsBox.Visibility = canBundle ? Visibility.Visible : Visibility.Collapsed;
        if (!canBundle)
            IncludeFlyToolsBox.IsChecked = false;

        var listActive = isFlyToolsTarget || (canBundle && IncludeFlyToolsBox.IsChecked == true);
        foreach (var tool in _flyTools)
        {
            var supported = FlyToolCatalog.SupportsPlatform(
                new FlyToolDescriptor(tool.Id, tool.DisplayName, tool.Platforms),
                platform);
            tool.IsEnabled = listActive && supported;
            if (!supported)
            {
                tool.IsChecked = false;
                tool.DisabledReason = platform == null
                    ? "不可用"
                    : $"不支持当前端（{platform}）";
            }
            else if (!listActive)
            {
                tool.DisabledReason = isFlyToolsTarget
                    ? tool.PlatformsText
                    : "请先勾选「集成插件工具」";
            }
            else
            {
                tool.DisabledReason = tool.PlatformsText;
            }
        }

        if (isFlyToolsTarget)
        {
            FlyToolsHint.Text = "直接打包 .flytool：勾选需要组包的插件（全部平台均可选）。";
        }
        else if (canBundle)
        {
            FlyToolsHint.Text = IncludeFlyToolsBox.IsChecked == true
                ? $"将把勾选的插件打进当前端（{platform}）产物的 Tools/Bundled；不支持本端的插件不可选。"
                : "默认不集成。勾选后可选择支持本端的插件，打进安装包/独立应用。";
        }
        else
        {
            FlyToolsHint.Text = "当前目标不支持插件工具。";
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择输出目录" };
        if (!string.IsNullOrWhiteSpace(OutputDirBox.Text) && Directory.Exists(OutputDirBox.Text))
            dlg.InitialDirectory = OutputDirBox.Text;
        if (dlg.ShowDialog(this) == true)
            OutputDirBox.Text = dlg.FolderName;
    }

    private void ResetVersion_Click(object sender, RoutedEventArgs e)
    {
        VersionBox.Text = VersionResolver.ReadCurrent(_repoRoot);
        AppendSystemLog($"版本号已重置为当前版本: {VersionBox.Text}");
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

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logFocusJob != null)
            _logFocusJob.ClearLogs();
        else
            _systemLogs.Clear();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
        => CopyLogLines(selectedOnly: true);

    private void CopyAllLog_Click(object sender, RoutedEventArgs e)
        => CopyLogLines(selectedOnly: false);

    private void LogList_CopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = LogList.Items.Count > 0;
        e.Handled = true;
    }

    private void LogList_CopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        CopyLogLines(selectedOnly: true);
        e.Handled = true;
    }

    private void CopyLogLines(bool selectedOnly)
    {
        IEnumerable<SetupLogEntry> entries;
        if (selectedOnly && LogList.SelectedItems.Count > 0)
            entries = LogList.SelectedItems.Cast<SetupLogEntry>();
        else
            entries = LogList.Items.Cast<SetupLogEntry>();

        var text = string.Join(Environment.NewLine, entries.Select(x => x.DisplayLine));
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            MessageDialog.Alert(this, "复制失败", ex.Message, MessageDialogKind.Warning);
        }
    }

    private void LogFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (QueueRowDef == null || LogRowDef == null || LogSplitterRowDef == null) return;

        if (!_logFullscreen)
        {
            _savedQueueHeight = QueueRowDef.Height;
            _savedLogHeight = LogRowDef.Height;
            _savedQueueMinHeight = QueueRowDef.MinHeight;
            _savedLogMinHeight = LogRowDef.MinHeight;

            QueuePanel.Visibility = Visibility.Collapsed;
            LogSplitter.Visibility = Visibility.Collapsed;
            QueueRowDef.MinHeight = 0;
            QueueRowDef.Height = new GridLength(0);
            LogSplitterRowDef.Height = new GridLength(0);
            LogRowDef.MinHeight = 0;
            LogRowDef.Height = new GridLength(1, GridUnitType.Star);

            LogFullscreenButton.Content = "还原";
            LogFullscreenButton.ToolTip = "还原队列与日志分栏";
            _logFullscreen = true;
        }
        else
        {
            QueuePanel.Visibility = Visibility.Visible;
            LogSplitter.Visibility = Visibility.Visible;
            QueueRowDef.MinHeight = _savedQueueMinHeight;
            QueueRowDef.Height = _savedQueueHeight;
            LogSplitterRowDef.Height = new GridLength(8);
            LogRowDef.MinHeight = _savedLogMinHeight;
            LogRowDef.Height = _savedLogHeight;

            LogFullscreenButton.Content = "全屏";
            LogFullscreenButton.ToolTip = "日志区占满右侧（隐藏队列）；再点还原";
            _logFullscreen = false;
        }

        Dispatcher.BeginInvoke(ScrollLogToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        ApplyMutexRules();
        var selected = _targets.FirstOrDefault(t => t.IsSelected && t.IsEnabled);
        if (selected == null)
        {
            MessageDialog.Alert(this, "提示", "请选择一个可用的打包目标。", MessageDialogKind.Info);
            return;
        }

        string version;
        try
        {
            version = VersionResolver.Normalize(VersionBox.Text);
        }
        catch (Exception ex)
        {
            MessageDialog.Alert(this, "提示", ex.Message, MessageDialogKind.Info);
            VersionBox.Focus();
            return;
        }

        selected.ApplySelectedFrameworkRuntime();
        var snapshot = selected.CloneForPublish();
        if (snapshot.IsRelayDocker && !TryValidateDockerOptions(out var dockerError))
        {
            MessageDialog.Alert(this, "提示", dockerError, MessageDialogKind.Info);
            return;
        }

        if (!TryValidateFlyToolsSelection(selected, out var flyToolsError))
        {
            MessageDialog.Alert(this, "提示", flyToolsError, MessageDialogKind.Info);
            return;
        }

        var options = BuildOptions(version).Clone();

        var job = new PublishJobItem
        {
            Title = snapshot.DisplayName,
            Subtitle = $"v{options.Version} · {snapshot.Rid} · {snapshot.SelectedFramework ?? snapshot.Framework}",
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

    private bool TryValidateDockerOptions(out string error)
    {
        error = "";
        TrySplitHostPortFromIpBox();
        if (IsLocalDockerHostUi(DockerHostBox?.Text))
            return true;

        if (!int.TryParse((DockerPortBox?.Text ?? "").Trim(), out var port) || port is < 1 or > 65535)
        {
            error = "端口须为 1–65535（TCP 常用 2375，SSH 常用 22）。";
            DockerPortBox?.Focus();
            return false;
        }

        var mode = GetDockerAuthMode();
        if (mode == "tcp")
            return true;

        if (string.IsNullOrWhiteSpace(DockerUserBox?.Text))
        {
            error = "SSH 模式请填写用户名；若仅局域网 TCP，请选「无认证 (TCP)」。";
            DockerUserBox?.Focus();
            return false;
        }

        if (mode == "key")
        {
            var key = DockerKeyBox?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(key) || !File.Exists(key))
            {
                error = "请选择有效的 SSH 私钥文件。";
                DockerKeyBox?.FocusInput();
                return false;
            }
        }
        else if (string.IsNullOrEmpty(DockerPasswordBox?.Password))
        {
            error = "请填写 SSH 密码，或改用密钥 / TCP 无认证。";
            DockerPasswordBox?.FocusInput();
            return false;
        }

        return true;
    }

    private PublishOptions BuildOptions(string version)
    {
        TrySplitHostPortFromIpBox();
        var host = DockerHostBox?.Text?.Trim() ?? "";
        var local = IsLocalDockerHostUi(host);
        var mode = local ? "local" : GetDockerAuthMode();
        _ = int.TryParse((DockerPortBox?.Text ?? "").Trim(), out var port);
        if (port is < 1 or > 65535)
            port = mode is "key" or "password" ? 22 : 2375;

        return new PublishOptions
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
                                && _targets.Any(t => t.IsSelected && t.IsEnabled && t.SupportsVddOffline),
            IncludeFlyTools = IncludeFlyToolsBox.IsChecked == true
                              && _targets.Any(t => t.IsSelected && t.IsEnabled && t.SupportsFlyToolsBundle),
            SelectedFlyToolIds = CollectSelectedFlyToolIds(),
            Version = version,
            DockerSshHost = local ? "" : host,
            DockerSshPort = port,
            DockerSshUser = mode is "key" or "password" ? (DockerUserBox?.Text?.Trim() ?? "") : "",
            DockerSshKeyPath = mode == "key" ? (DockerKeyBox?.Text?.Trim() ?? "") : "",
            DockerSshPassword = mode == "password" ? (DockerPasswordBox?.Password ?? "") : "",
            DockerUseTcp = mode == "tcp"
        };
    }

    private List<string> CollectSelectedFlyToolIds()
        => _flyTools.Where(t => t.IsChecked && t.IsEnabled).Select(t => t.Id).ToList();

    private bool TryValidateFlyToolsSelection(PublishTargetItem selected, out string error)
    {
        error = "";
        var ids = CollectSelectedFlyToolIds();
        if (selected.IsFlyTools)
        {
            if (ids.Count == 0)
            {
                error = "请至少勾选一个要打包的插件工具。";
                return false;
            }

            return true;
        }

        if (selected.SupportsFlyToolsBundle && IncludeFlyToolsBox.IsChecked == true && ids.Count == 0)
        {
            error = "已勾选集成插件工具，请至少选择一个支持本端的插件。";
            return false;
        }

        return true;
    }
}
