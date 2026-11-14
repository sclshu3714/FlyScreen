using System.Collections.ObjectModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy.Ui.Maui;

/// <summary>端口代理工作区（Maui；工具条 + 列表，支持批量删除与行内编辑/删除）。</summary>
public sealed class PortProxyPanel : ContentView
{
    private readonly IPortProxyService? _service;
    private readonly ObservableCollection<PortProxyRuleRow> _rows = new();
    private readonly Label _hint;
    private readonly Label _admin;
    private readonly Label _status;
    private readonly Button _addBtn;
    private readonly Button _deleteBtn;
    private readonly Button _refreshBtn;
    private readonly Button _resetBtn;
    private readonly Button _dumpBtn;
    private readonly CheckBox _selectAll;
    private readonly PortProxyDropDown _filterDrop;
    private PortProxyMappingKind? _filterKind;
    private bool _busy;
    private bool _suppressSelectAll;

    public Action<string>? StatusReporter { get; set; }

    public PortProxyPanel()
    {
        _service = ToolService.Instance.PortProxy
                   ?? ToolService.Instance.GetToolService<IPortProxyService>();

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        var muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        var line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        var subtle = Color.FromArgb(dark ? "#0F172A" : "#F3F4F6");
        var card = Color.FromArgb(dark ? "#1E293B" : "#FFFFFF");
        var blue = Color.FromArgb("#2563EB");
        var warn = Color.FromArgb("#DC2626");

        _hint = new Label
        {
            Text = Loc.T("Tool.PortProxy.Hint"),
            FontSize = 12,
            TextColor = muted,
            LineBreakMode = LineBreakMode.WordWrap
        };
        _admin = new Label { FontSize = 12, TextColor = text, LineBreakMode = LineBreakMode.WordWrap };
        _status = new Label { FontSize = 12, TextColor = muted };

        _addBtn = MakeBtn(Loc.T("Tool.PortProxy.Add"), blue, Colors.White);
        _deleteBtn = MakeBtn(Loc.T("Tool.PortProxy.Delete"), card, warn, line);
        _refreshBtn = MakeBtn(Loc.T("Tool.PortProxy.Refresh"), card, text, line);
        _resetBtn = MakeBtn(Loc.T("Tool.PortProxy.Reset"), card, warn, line);
        _dumpBtn = MakeBtn(Loc.T("Tool.PortProxy.Dump"), card, text, line);
        _addBtn.Clicked += async (_, _) => await AddAsync();
        _deleteBtn.Clicked += async (_, _) => await DeleteSelectedAsync();
        _refreshBtn.Clicked += async (_, _) => await RefreshAsync();
        _resetBtn.Clicked += async (_, _) => await ResetAsync();
        _dumpBtn.Clicked += async (_, _) => await DumpAsync();

        _selectAll = new CheckBox { VerticalOptions = LayoutOptions.Center };
        _selectAll.CheckedChanged += (_, e) =>
        {
            if (_suppressSelectAll) return;
            foreach (var row in _rows)
                row.IsSelected = e.Value;
        };
        var selectAllLabel = new Label
        {
            Text = Loc.T("Tool.PortProxy.SelectAll"),
            VerticalOptions = LayoutOptions.Center,
            TextColor = text,
            FontSize = 13
        };
        var selectAllTap = new TapGestureRecognizer();
        selectAllTap.Tapped += (_, _) =>
        {
            _selectAll.IsChecked = !_selectAll.IsChecked;
        };
        selectAllLabel.GestureRecognizers.Add(selectAllTap);

        _filterKind = null;
        var allLabel = Loc.T("Tool.PortProxy.TabAll");
        _filterDrop = new PortProxyDropDown(text, muted, line, card)
        {
            Value = allLabel,
            WidthRequest = 140,
            MinimumWidthRequest = 128,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start
        };
        _filterDrop.SetOptions(allLabel, "v4tov4", "v4tov6", "v6tov4", "v6tov6");
        _filterDrop.SelectionChanged += OnFilterChanged;

        var adminBorder = new Border
        {
            Stroke = line,
            BackgroundColor = subtle,
            Padding = new Thickness(10, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = _admin
        };

        var toolbar = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            BackgroundColor = card,
            Padding = new Thickness(10, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new FlexLayout
                    {
                        Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                        AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
                        Children = { _addBtn, _deleteBtn, _refreshBtn, _resetBtn, _dumpBtn }
                    },
                    new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(GridLength.Auto),
                            new ColumnDefinition(GridLength.Auto),
                            new ColumnDefinition(GridLength.Star),
                            new ColumnDefinition(GridLength.Auto),
                            new ColumnDefinition(GridLength.Auto)
                        },
                        ColumnSpacing = 8,
                        Children =
                        {
                            _selectAll,
                            Col(selectAllLabel, 1),
                            Col(new Label
                            {
                                Text = Loc.T("Tool.PortProxy.Filter"),
                                VerticalOptions = LayoutOptions.Center,
                                TextColor = muted,
                                FontSize = 12,
                                HorizontalOptions = LayoutOptions.End
                            }, 3),
                            Col(_filterDrop, 4)
                        }
                    }
                }
            }
        };

        var list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.None,
            EmptyView = new Label
            {
                Text = Loc.T("Tool.PortProxy.Empty"),
                TextColor = muted,
                Margin = new Thickness(12),
                HorizontalOptions = LayoutOptions.Center
            },
            ItemTemplate = new DataTemplate(() => BuildRow(text, muted, line, card, warn))
        };

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Padding = new Thickness(4, 0, 4, 4),
            RowSpacing = 8
        };
        root.Add(_hint, 0, 0);
        root.Add(adminBorder, 0, 1);
        root.Add(toolbar, 0, 2);
        root.Add(list, 0, 3);
        root.Add(_status, 0, 4);

        var flyoutLayer = new AbsoluteLayout
        {
            IsVisible = false,
            BackgroundColor = Colors.Transparent,
            ZIndex = 200
        };
        var shell = new Grid();
        shell.Add(root);
        shell.Add(flyoutLayer);
        Content = shell;
        _filterDrop.BindFlyout(new PortProxyFlyoutHost(flyoutLayer));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (_service == null)
        {
            _hint.Text = Loc.T("Tool.PortProxy.NotReady");
            _status.Text = Loc.T("Tool.PortProxy.NotReady");
            SetBusy(true);
            return;
        }

        UpdateAdmin();
        BindRules();
    }

    private View BuildRow(Color text, Color muted, Color line, Color card, Color warn)
    {
        var check = new CheckBox { VerticalOptions = LayoutOptions.Center };
        check.SetBinding(CheckBox.IsCheckedProperty, nameof(PortProxyRuleRow.IsSelected));

        var summary = new Label
        {
            FontSize = 13,
            TextColor = text,
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Center
        };
        summary.SetBinding(Label.TextProperty, nameof(PortProxyRuleRow.Summary));

        var edit = new Button
        {
            Text = Loc.T("Tool.PortProxy.Edit"),
            FontSize = 12,
            Padding = new Thickness(10, 4),
            BackgroundColor = card,
            TextColor = text,
            BorderColor = line,
            BorderWidth = 1
        };
        edit.SetBinding(Button.CommandParameterProperty, ".");
        edit.Clicked += async (s, _) =>
        {
            if (s is Button { CommandParameter: PortProxyRuleRow row })
                await EditAsync(row.Rule);
        };

        var del = new Button
        {
            Text = Loc.T("Tool.PortProxy.Delete"),
            FontSize = 12,
            Padding = new Thickness(10, 4),
            BackgroundColor = card,
            TextColor = warn,
            BorderColor = Color.FromArgb("#FECACA"),
            BorderWidth = 1
        };
        del.SetBinding(Button.CommandParameterProperty, ".");
        del.Clicked += async (s, _) =>
        {
            if (s is Button { CommandParameter: PortProxyRuleRow row })
                await DeleteOneAsync(row.Rule);
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8
        };
        grid.Add(check, 0, 0);
        grid.Add(summary, 1, 0);
        grid.Add(edit, 2, 0);
        grid.Add(del, 3, 0);

        return new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            BackgroundColor = card,
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = grid
        };
    }

    private static Button MakeBtn(string text, Color bg, Color fg, Color? border = null)
        => new()
        {
            Text = text,
            BackgroundColor = bg,
            TextColor = fg,
            BorderColor = border ?? bg,
            BorderWidth = border == null ? 0 : 1,
            Padding = new Thickness(12, 6),
            Margin = new Thickness(0, 0, 8, 4),
            FontSize = 13
        };

    private static View Col(View view, int column)
    {
        Grid.SetColumn(view, column);
        return view;
    }

    private void OnFilterChanged(string choice)
    {
        var all = Loc.T("Tool.PortProxy.TabAll");
        if (string.IsNullOrWhiteSpace(choice) || choice == all)
            _filterKind = null;
        else
        {
            _filterKind = choice switch
            {
                "v4tov6" => PortProxyMappingKind.V4ToV6,
                "v6tov4" => PortProxyMappingKind.V6ToV4,
                "v6tov6" => PortProxyMappingKind.V6ToV6,
                _ => PortProxyMappingKind.V4ToV4
            };
        }

        BindRules();
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_service == null) return;
        _service.RulesChanged += OnRulesChanged;
        _service.SessionChanged += OnSessionChanged;
        _ = RefreshAsync();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_service == null) return;
        _service.RulesChanged -= OnRulesChanged;
        _service.SessionChanged -= OnSessionChanged;
    }

    private void OnRulesChanged()
        => MainThread.BeginInvokeOnMainThread(BindRules);

    private void OnSessionChanged()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_service == null) return;
            UpdateAdmin();
            _status.Text = _service.LastStatus;
            if (!string.IsNullOrWhiteSpace(_service.LastStatus))
                StatusReporter?.Invoke(_service.LastStatus);
        });

    private void UpdateAdmin()
    {
        if (_service == null) return;
        _admin.Text = _service.IsElevated
            ? Loc.T("Tool.PortProxy.AdminOk")
            : Loc.T("Tool.PortProxy.AdminNeed");
    }

    private void BindRules()
    {
        if (_service == null) return;
        var filter = CurrentFilter();
        var selected = _rows.Where(r => r.IsSelected)
            .Select(r => $"{r.KindToken}|{r.ListenAddress}|{r.ListenPort}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        foreach (var rule in _service.Rules)
        {
            if (filter is not null && rule.Kind != filter.Value) continue;
            _rows.Add(new PortProxyRuleRow(rule)
            {
                IsSelected = selected.Contains($"{rule.KindToken}|{rule.ListenAddress}|{rule.ListenPort}")
            });
        }

        SyncSelectAll();
        _status.Text = string.IsNullOrWhiteSpace(_service.LastStatus)
            ? (_rows.Count == 0 ? Loc.T("Tool.PortProxy.Empty") : $"共 {_rows.Count} 条")
            : _service.LastStatus;
    }

    private PortProxyMappingKind? CurrentFilter() => _filterKind;

    private void SyncSelectAll()
    {
        _suppressSelectAll = true;
        try
        {
            if (_rows.Count == 0) { _selectAll.IsChecked = false; return; }
            var n = _rows.Count(r => r.IsSelected);
            _selectAll.IsChecked = n > 0 && n == _rows.Count;
        }
        finally { _suppressSelectAll = false; }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        var on = !busy && _service != null;
        _addBtn.IsEnabled = on;
        _deleteBtn.IsEnabled = on;
        _refreshBtn.IsEnabled = on;
        _resetBtn.IsEnabled = on;
        _dumpBtn.IsEnabled = on;
    }

    private void Report(string msg)
    {
        _status.Text = msg;
        StatusReporter?.Invoke(msg);
    }

    private async Task RefreshAsync()
    {
        if (_service == null || _busy) return;
        SetBusy(true);
        try
        {
            await _service.RefreshAsync();
            BindRules();
        }
        catch (Exception ex) { Report(ex.Message); }
        finally { SetBusy(false); }
    }

    private async Task AddAsync()
    {
        if (_service == null) return;
        var seed = new PortProxyRuleInfo
        {
            Kind = CurrentFilter() ?? PortProxyMappingKind.V4ToV4,
            ListenAddress = "0.0.0.0",
            ConnectAddress = "127.0.0.1",
            Protocol = "tcp"
        };
        var result = await PortProxyRulePrompt.ShowAsync(GetNavigation(), isEdit: false, seed);
        if (result == null) return;
        SetBusy(true);
        try
        {
            var r = await _service.AddAsync(result);
            Report(r.Ok ? Loc.T("Tool.PortProxy.Add") + " · OK" : r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private async Task EditAsync(PortProxyRuleInfo rule)
    {
        if (_service == null) return;
        var result = await PortProxyRulePrompt.ShowAsync(GetNavigation(), isEdit: true, rule.Clone());
        if (result == null) return;
        SetBusy(true);
        try
        {
            var r = await _service.SetAsync(result);
            Report(r.Ok ? Loc.T("Tool.PortProxy.Edit") + " · OK" : r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private async Task DeleteOneAsync(PortProxyRuleInfo rule)
    {
        if (_service == null) return;
        if (!await ConfirmDeleteAsync([rule])) return;
        SetBusy(true);
        try
        {
            var r = await _service.DeleteAsync(rule);
            Report(r.Ok ? Loc.T("Tool.PortProxy.Delete") + " · OK" : r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_service == null) return;
        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Rule).ToList();
        if (selected.Count == 0)
        {
            Report(Loc.T("Tool.PortProxy.SelectFirst"));
            return;
        }

        if (!await ConfirmDeleteAsync(selected)) return;
        SetBusy(true);
        try
        {
            var r = await _service.DeleteManyAsync(selected);
            Report(r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private async Task ResetAsync()
    {
        if (_service == null) return;
        var page = GetParentPage();
        if (page == null) return;
        var ok = await page.DisplayAlertAsync(
            Loc.T("Tool.PortProxy.Name"),
            Loc.T("Tool.PortProxy.ConfirmReset"),
            Loc.T("Tool.PortProxy.Ok"),
            Loc.T("Tool.PortProxy.Cancel"));
        if (!ok) return;
        SetBusy(true);
        try
        {
            var r = await _service.ResetAsync();
            Report(r.Ok ? Loc.T("Tool.PortProxy.Reset") + " · OK" : r.Message);
            BindRules();
        }
        finally { SetBusy(false); }
    }

    private async Task DumpAsync()
    {
        if (_service == null) return;
        SetBusy(true);
        try
        {
            var r = await _service.DumpAsync();
            if (!r.Ok)
            {
                Report(r.Message);
                return;
            }

            var text = string.IsNullOrWhiteSpace(r.StdOut) ? r.Message : r.StdOut;
            var page = GetParentPage();
            if (page == null) return;
            await page.DisplayAlertAsync(Loc.T("Tool.PortProxy.DialogDump"), text, Loc.T("Tool.PortProxy.Close"));
            try { await Clipboard.Default.SetTextAsync(text); }
            catch { /* ignore */ }
        }
        finally { SetBusy(false); }
    }

    private async Task<bool> ConfirmDeleteAsync(IReadOnlyList<PortProxyRuleInfo> rules)
    {
        var page = GetParentPage();
        if (page == null) return false;
        var detail = rules.Count == 1
            ? $"{rules[0].KindToken} {rules[0].ListenAddress}:{rules[0].ListenPort} → {rules[0].ConnectAddress}:{rules[0].ConnectPort}"
            : string.Format(Loc.T("Tool.PortProxy.ConfirmDeleteMany"), rules.Count);
        return await page.DisplayAlertAsync(
            Loc.T("Tool.PortProxy.Name"),
            Loc.T("Tool.PortProxy.ConfirmDelete") + "\n" + detail,
            Loc.T("Tool.PortProxy.Ok"),
            Loc.T("Tool.PortProxy.Cancel"));
    }

    private INavigation? GetNavigation() => GetParentPage()?.Navigation;

    private Page? GetParentPage()
    {
        Element? e = this;
        while (e != null)
        {
            if (e is Page p) return p;
            e = e.Parent;
        }

        return Application.Current?.Windows.FirstOrDefault()?.Page;
    }
}
