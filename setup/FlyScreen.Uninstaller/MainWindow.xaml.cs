using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyScreen.ToolSdk;
using FlyScreen.Uninstaller.Controls;
using FlyScreen.Uninstaller.Services;

namespace FlyScreen.Uninstaller;

public partial class MainWindow : Window
{
    private readonly UninstallContext _ctx;
    private readonly bool _silent;
    private readonly IReadOnlyList<InstalledToolsProbe.ToolInfo> _tools;
    private readonly Dictionary<string, CheckBox> _toolChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolUninstallSchemaPanel?> _schemaPanels = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private bool _done;
    private CancellationTokenSource? _uninstallCts;

    public MainWindow(UninstallContext ctx, bool silent)
    {
        InitializeComponent();
        _ctx = ctx;
        _silent = silent;
        _tools = InstalledToolsProbe.ListInstalled(ctx.InstallDir);

        Title = $"卸载 {_ctx.ProductName}";
        TitleRun.Text = $"卸载 {_ctx.ProductName}";
        VersionRun.Text = string.IsNullOrWhiteSpace(_ctx.Version) ? "" : $"v{_ctx.Version}";
        PathText.Text = _ctx.InstallDir;

        BuildToolsList();
        Mode_Changed(this, new RoutedEventArgs());

        if (!_ctx.Found)
        {
            StatusText.Text = "未找到安装目录，仍可清理注册表与快捷方式。";
            ProgressPanel.Visibility = Visibility.Visible;
        }

        if (_silent)
            Loaded += async (_, _) => await RunUninstallAsync().ConfigureAwait(true);
    }

    private void BuildToolsList()
    {
        ToolsList.Children.Clear();
        _toolChecks.Clear();
        _schemaPanels.Clear();

        // 无已装插件：不显示「卸载范围」，直接按卸载主程序处理
        if (_tools.Count == 0)
        {
            ScopePanel.Visibility = Visibility.Collapsed;
            ModeProduct.IsChecked = true;
            return;
        }

        ScopePanel.Visibility = Visibility.Visible;
        ToolsPanel.Visibility = Visibility.Visible;
        foreach (var tool in _tools)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var cb = new CheckBox
            {
                Style = (Style)FindResource("ModernCheckBox"),
                IsChecked = true,
                Tag = tool.Id
            };
            cb.Content = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(tool.Version)
                    ? tool.DisplayName
                    : $"{tool.DisplayName}  ({tool.Version})",
                TextWrapping = TextWrapping.Wrap
            };
            cb.Checked += (_, _) => RefreshSchemaVisibility();
            cb.Unchecked += (_, _) => RefreshSchemaVisibility();
            _toolChecks[tool.Id] = cb;
            block.Children.Add(cb);

            if (tool.UninstallUi != null)
            {
                var panel = new ToolUninstallSchemaPanel(tool.UninstallUi) { Visibility = Visibility.Visible };
                _schemaPanels[tool.Id] = panel;
                block.Children.Add(panel);
            }
            else
                _schemaPanels[tool.Id] = null;

            ToolsList.Children.Add(block);
        }
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (KeepDataBox == null || ModeProduct == null) return;

        KeepDataBox.Visibility = ModeProduct.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshSchemaVisibility();
    }

    private void RefreshSchemaVisibility()
    {
        foreach (var (id, panel) in _schemaPanels)
        {
            if (panel == null) continue;
            var selected = _toolChecks.TryGetValue(id, out var cb) && cb.IsChecked == true;
            panel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SelectAllTools_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _toolChecks.Values)
            cb.IsChecked = true;
    }

    private void SelectNoneTools_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _toolChecks.Values)
            cb.IsChecked = false;
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d)
        {
            while (d != null)
            {
                if (d is Button or CheckBox or RadioButton or PasswordBox or TextBox)
                    return;
                d = VisualTreeHelper.GetParent(d);
            }
        }
        try { DragMove(); } catch { /* ignore */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            var abort = MessageBox.Show(this,
                "正在卸载中，确定要中止吗？\n（已执行的步骤可能不会回滚）",
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (abort != MessageBoxResult.Yes) return;
            try { _uninstallCts?.Cancel(); } catch { /* ignore */ }
            StatusText.Text = "正在中止…";
            StatusText.Foreground = (Brush)FindResource("MutedBrush");
            return;
        }

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

    private UninstallMode CurrentMode()
    {
        if (_tools.Count == 0) return UninstallMode.Product;
        if (ModeToolsOnly.IsChecked == true) return UninstallMode.ToolsOnly;
        return UninstallMode.Product;
    }

    private bool TryBuildBatch(out ToolUninstallBatchFile batch)
    {
        batch = new ToolUninstallBatchFile();
        var mode = CurrentMode();
        foreach (var tool in _tools)
        {
            var selected = _toolChecks.TryGetValue(tool.Id, out var cb) && cb.IsChecked == true;
            if (!selected) continue;

            var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (tool.UninstallUi != null)
            {
                if (_silent)
                {
                    foreach (var kv in tool.UninstallUi.Defaults)
                        answers[kv.Key] = kv.Value;
                }
                else if (_schemaPanels.TryGetValue(tool.Id, out var panel) && panel != null)
                {
                    var missing = panel.Validate();
                    if (missing != null)
                    {
                        MessageBox.Show(this, $"插件「{tool.DisplayName}」请填写：{missing}",
                            Title, MessageBoxButton.OK, MessageBoxImage.Information);
                        return false;
                    }
                    foreach (var kv in panel.Values)
                        answers[kv.Key] = kv.Value;
                }
            }

            batch.Tools.Add(new ToolUninstallBatchItem { Id = tool.Id, Answers = answers });
        }

        if (mode == UninstallMode.ToolsOnly && batch.Tools.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一个要卸载的插件。", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private async Task RunUninstallAsync()
    {
        if (_busy || _done) return;

        if (!TryBuildBatch(out var batch))
            return;

        var mode = _silent ? UninstallMode.Product : CurrentMode();
        if (_silent)
        {
            batch = new ToolUninstallBatchFile();
            foreach (var tool in _tools)
            {
                var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (tool.UninstallUi != null)
                {
                    foreach (var kv in tool.UninstallUi.Defaults)
                        answers[kv.Key] = kv.Value;
                }
                batch.Tools.Add(new ToolUninstallBatchItem { Id = tool.Id, Answers = answers });
            }
        }

        var keepData = KeepDataBox.IsChecked == true;

        _uninstallCts?.Dispose();
        _uninstallCts = new CancellationTokenSource();
        var ct = _uninstallCts.Token;

        _busy = true;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        UninstallButton.IsEnabled = false;
        // 忙碌时仍可「取消」中止（结束卡住的主程序批处理）
        CancelButton.IsEnabled = true;
        CancelButton.Content = "取消";
        KeepDataBox.IsEnabled = false;
        ToolsPanel.IsEnabled = false;
        ModeProduct.IsEnabled = ModeToolsOnly.IsEnabled = false;

        try
        {
            var progress = new Progress<ProgressReport>(ApplyProgress);
            var service = new UninstallService();
            await service.UninstallAsync(_ctx, mode, batch, keepData, progress, ct).ConfigureAwait(true);

            _done = true;
            _busy = false;
            ApplyProgress(ProgressReport.Create(100, "完成"));
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
        catch (OperationCanceledException)
        {
            StatusText.Text = "已中止卸载。";
            StatusText.Foreground = (Brush)FindResource("MutedBrush");
            UninstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            KeepDataBox.IsEnabled = true;
            ToolsPanel.IsEnabled = true;
            ModeProduct.IsEnabled = ModeToolsOnly.IsEnabled = true;
            _busy = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "失败：" + ex.Message;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            UninstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            KeepDataBox.IsEnabled = true;
            ToolsPanel.IsEnabled = true;
            ModeProduct.IsEnabled = ModeToolsOnly.IsEnabled = true;
            _busy = false;
        }
        finally
        {
            _uninstallCts?.Dispose();
            _uninstallCts = null;
        }
    }
}
