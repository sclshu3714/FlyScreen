using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy.Ui.Win;

public partial class PortProxyPanel : UserControl
{
    private readonly IPortProxyService? _service;
    private readonly ObservableCollection<PortProxyRuleRow> _rows = new();
    private bool _busy;
    private bool _suppressSelectAll;
    private bool _suppressFilter;

    public Action<string>? StatusReporter { get; set; }

    public PortProxyPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.PortProxy
                   ?? ToolService.Instance.GetToolService<IPortProxyService>();
        RulesGrid.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyLoc();

        if (_service == null)
        {
            HintText.Text = Loc.T("Tool.PortProxy.NotReady");
            StatusText.Text = Loc.T("Tool.PortProxy.NotReady");
            SetBusy(true);
            return;
        }

        UpdateAdminBanner();
        BindRules();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        _service.RulesChanged += OnRulesChanged;
        _service.SessionChanged += OnSessionChanged;
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        _service.RulesChanged -= OnRulesChanged;
        _service.SessionChanged -= OnSessionChanged;
    }

    private void OnRulesChanged()
        => Dispatcher.BeginInvoke(BindRules);

    private void OnSessionChanged()
        => Dispatcher.BeginInvoke(() =>
        {
            if (_service == null) return;
            UpdateAdminBanner();
            StatusText.Text = _service.LastStatus;
            if (!string.IsNullOrWhiteSpace(_service.LastStatus))
                StatusReporter?.Invoke(_service.LastStatus);
        });

    private void ApplyLoc()
    {
        HintText.Text = Loc.T("Tool.PortProxy.Hint");
        RefreshBtn.Content = Loc.T("Tool.PortProxy.Refresh");
        AddBtn.Content = Loc.T("Tool.PortProxy.Add");
        DeleteBtn.Content = Loc.T("Tool.PortProxy.Delete");
        ResetBtn.Content = Loc.T("Tool.PortProxy.Reset");
        DumpBtn.Content = Loc.T("Tool.PortProxy.Dump");
        ColKind.Header = Loc.T("Tool.PortProxy.ColKind");
        ColListenAddress.Header = Loc.T("Tool.PortProxy.ColListenAddress");
        ColListenPort.Header = Loc.T("Tool.PortProxy.ColListenPort");
        ColConnectAddress.Header = Loc.T("Tool.PortProxy.ColConnectAddress");
        ColConnectPort.Header = Loc.T("Tool.PortProxy.ColConnectPort");
        ColProtocol.Header = Loc.T("Tool.PortProxy.ColProtocol");
        ColActions.Header = Loc.T("Tool.PortProxy.ColActions");
        FilterCombo.ToolTip = Loc.T("Tool.PortProxy.Filter");
        PopulateFilterCombo();
    }

    private void PopulateFilterCombo()
    {
        _suppressFilter = true;
        try
        {
            var selectedTag = (FilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            FilterCombo.Items.Clear();
            FilterCombo.Items.Add(new ComboBoxItem
            {
                Content = Loc.T("Tool.PortProxy.TabAll"),
                Tag = ""
            });
            foreach (var token in new[] { "v4tov4", "v4tov6", "v6tov4", "v6tov6" })
                FilterCombo.Items.Add(new ComboBoxItem { Content = token, Tag = token });

            if (!string.IsNullOrEmpty(selectedTag))
            {
                for (var i = 0; i < FilterCombo.Items.Count; i++)
                {
                    if (FilterCombo.Items[i] is ComboBoxItem { Tag: string t }
                        && string.Equals(t, selectedTag, StringComparison.OrdinalIgnoreCase))
                    {
                        FilterCombo.SelectedIndex = i;
                        return;
                    }
                }
            }

            FilterCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressFilter = false;
        }
    }

    private void UpdateAdminBanner()
    {
        if (_service == null) return;
        AdminText.Text = _service.IsElevated
            ? Loc.T("Tool.PortProxy.AdminOk")
            : Loc.T("Tool.PortProxy.AdminNeed");
    }

    private void BindRules()
    {
        if (_service == null) return;
        var filter = CurrentFilter();
        var selectedKeys = _rows.Where(r => r.IsSelected).Select(RowKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        foreach (var rule in _service.Rules)
        {
            if (filter is not null && rule.Kind != filter.Value)
                continue;
            var row = new PortProxyRuleRow(rule)
            {
                IsSelected = selectedKeys.Contains(RowKey(rule))
            };
            _rows.Add(row);
        }

        SyncSelectAllBox();
        StatusText.Text = string.IsNullOrWhiteSpace(_service.LastStatus)
            ? (_rows.Count == 0 ? Loc.T("Tool.PortProxy.Empty") : $"共 {_rows.Count} 条")
            : _service.LastStatus;
    }

    private static string RowKey(PortProxyRuleRow row) => RowKey(row.Rule);

    private static string RowKey(PortProxyRuleInfo rule)
        => $"{rule.KindToken}|{rule.ListenAddress}|{rule.ListenPort}|{rule.Protocol}";

    private PortProxyMappingKind? CurrentFilter()
    {
        var tag = (FilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(tag)) return null;
        return tag.ToLowerInvariant() switch
        {
            "v4tov4" => PortProxyMappingKind.V4ToV4,
            "v4tov6" => PortProxyMappingKind.V4ToV6,
            "v6tov4" => PortProxyMappingKind.V6ToV4,
            "v6tov6" => PortProxyMappingKind.V6ToV6,
            _ => null
        };
    }

    private async Task RefreshAsync()
    {
        if (_service == null || _busy) return;
        _busy = true;
        SetBusy(true);
        try
        {
            await _service.RefreshAsync().ConfigureAwait(true);
            BindRules();
        }
        catch (Exception ex)
        {
            Report(ex.Message);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        var enable = !busy && _service != null;
        RefreshBtn.IsEnabled = enable;
        AddBtn.IsEnabled = enable;
        DeleteBtn.IsEnabled = enable;
        ResetBtn.IsEnabled = enable;
        DumpBtn.IsEnabled = enable;
        FilterCombo.IsEnabled = enable;
    }

    private void Report(string msg)
    {
        StatusText.Text = msg;
        StatusReporter?.Invoke(msg);
    }

    private Window? OwnerWindow() => Window.GetWindow(this);

    private IReadOnlyList<PortProxyRuleInfo> SelectedRules()
        => _rows.Where(r => r.IsSelected).Select(r => r.Rule).ToList();

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter || !IsLoaded || _service == null) return;
        BindRules();
    }

    private void SelectAllBox_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAll) return;
        var on = SelectAllBox.IsChecked == true;
        foreach (var row in _rows)
            row.IsSelected = on;
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e)
        => SyncSelectAllBox();

    private void SyncSelectAllBox()
    {
        _suppressSelectAll = true;
        try
        {
            if (_rows.Count == 0)
            {
                SelectAllBox.IsChecked = false;
                return;
            }

            var selected = _rows.Count(r => r.IsSelected);
            SelectAllBox.IsChecked = selected == 0 ? false
                : selected == _rows.Count ? true
                : null;
        }
        finally { _suppressSelectAll = false; }
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        => _ = RefreshAsync();

    private async void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        var seedKind = CurrentFilter() ?? PortProxyMappingKind.V4ToV4;
        var dlg = new PortProxyRuleDialog(isEdit: false, seed: new PortProxyRuleInfo
        {
            Kind = seedKind,
            ListenAddress = "0.0.0.0",
            ListenPort = "",
            ConnectAddress = "127.0.0.1",
            ConnectPort = "",
            Protocol = "tcp"
        })
        {
            Owner = OwnerWindow()
        };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;

        SetBusy(true);
        try
        {
            var r = await _service.AddAsync(dlg.Result).ConfigureAwait(true);
            Report(r.Ok ? Loc.T("Tool.PortProxy.Add") + " · OK" : r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private void RulesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RulesGrid.SelectedItem is PortProxyRuleRow row)
            _ = EditRuleAsync(row.Rule);
    }

    private void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PortProxyRuleRow row })
            _ = EditRuleAsync(row.Rule);
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        if (sender is not FrameworkElement { Tag: PortProxyRuleRow row }) return;
        if (!ConfirmDelete([row.Rule])) return;

        SetBusy(true);
        try
        {
            var r = await _service.DeleteAsync(row.Rule).ConfigureAwait(true);
            Report(r.Ok ? Loc.T("Tool.PortProxy.Delete") + " · OK" : r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        var selected = SelectedRules();
        if (selected.Count == 0)
        {
            Report(Loc.T("Tool.PortProxy.SelectFirst"));
            return;
        }

        if (!ConfirmDelete(selected)) return;

        SetBusy(true);
        try
        {
            var r = await _service.DeleteManyAsync(selected).ConfigureAwait(true);
            Report(r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private async Task EditRuleAsync(PortProxyRuleInfo rule)
    {
        if (_service == null) return;
        var dlg = new PortProxyRuleDialog(isEdit: true, seed: rule.Clone())
        {
            Owner = OwnerWindow()
        };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;

        SetBusy(true);
        try
        {
            var r = await _service.SetAsync(dlg.Result).ConfigureAwait(true);
            Report(r.Ok ? Loc.T("Tool.PortProxy.Edit") + " · OK" : r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private bool ConfirmDelete(IReadOnlyList<PortProxyRuleInfo> rules)
    {
        var detail = rules.Count == 1
            ? $"{rules[0].KindToken} {rules[0].ListenAddress}:{rules[0].ListenPort} → {rules[0].ConnectAddress}:{rules[0].ConnectPort}"
            : string.Format(Loc.T("Tool.PortProxy.ConfirmDeleteMany"), rules.Count);
        var confirm = System.Windows.MessageBox.Show(
            OwnerWindow(),
            Loc.T("Tool.PortProxy.ConfirmDelete") + "\n" + detail,
            Loc.T("Tool.PortProxy.Name"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return confirm == MessageBoxResult.Yes;
    }

    private async void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        var confirm = System.Windows.MessageBox.Show(
            OwnerWindow(),
            Loc.T("Tool.PortProxy.ConfirmReset"),
            Loc.T("Tool.PortProxy.Name"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var r = await _service.ResetAsync().ConfigureAwait(true);
            Report(r.Ok ? Loc.T("Tool.PortProxy.Reset") + " · OK" : r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private async void DumpBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        SetBusy(true);
        try
        {
            var r = await _service.DumpAsync().ConfigureAwait(true);
            if (!r.Ok)
            {
                Report(r.Message);
                return;
            }

            var text = string.IsNullOrWhiteSpace(r.StdOut) ? r.Message : r.StdOut;
            var dlg = new PortProxyDumpDialog(text) { Owner = OwnerWindow() };
            dlg.ShowDialog();
        }
        finally { SetBusy(false); }
    }
}
