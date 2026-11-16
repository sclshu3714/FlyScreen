using System.Collections.ObjectModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy.Ui.Maui;

/// <summary>端口代理工作区（Maui；对齐 Win 工具条 + 表头列 + 行内操作）。</summary>
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
    private readonly CollectionView _table;
    private readonly Color _text;
    private readonly Color _muted;
    private readonly Color _line;
    private readonly Color _subtle;
    private readonly Color _card;
    private readonly Color _blue;
    private readonly Color _warn;
    private PortProxyMappingKind? _filterKind;
    private bool _busy;
    private bool _suppressSelectAll;

    public Action<string>? StatusReporter { get; set; }

    public PortProxyPanel()
    {
        _service = ToolService.Instance.PortProxy
                   ?? ToolService.Instance.GetToolService<IPortProxyService>();

        _text = ResolveColor("Text", "#111827");
        _muted = ResolveColor("Muted", "#6B7280");
        _line = ResolveColor("Line", "#E5E7EB");
        _subtle = ResolveColor("SubtleBg", "#F3F4F6");
        _card = ResolveColor("CardBg", "#FFFFFF");
        _blue = ResolveColor("Blue", "#2563EB");
        _warn = ResolveColor("Warn", "#DC2626");

        _hint = new Label
        {
            Text = Loc.T("Tool.PortProxy.Hint"),
            FontSize = 12,
            TextColor = _muted,
            LineBreakMode = LineBreakMode.WordWrap
        };
        _admin = new Label { FontSize = 12, TextColor = _text, LineBreakMode = LineBreakMode.WordWrap };
        _status = new Label { FontSize = 12, TextColor = _muted };

        _addBtn = MakeToolBarBtn(Loc.T("Tool.PortProxy.Add"), _blue, Colors.White);
        _deleteBtn = MakeToolBarBtn(Loc.T("Tool.PortProxy.Delete"), _card, _warn, _line);
        _refreshBtn = MakeToolBarBtn(Loc.T("Tool.PortProxy.Refresh"), _card, _text, _line);
        _resetBtn = MakeToolBarBtn(Loc.T("Tool.PortProxy.Reset"), _card, _warn, _line);
        _dumpBtn = MakeToolBarBtn(Loc.T("Tool.PortProxy.Dump"), _card, _text, _line, marginRight: 0);
        _addBtn.Clicked += async (_, _) => await AddAsync();
        _deleteBtn.Clicked += async (_, _) => await DeleteSelectedAsync();
        _refreshBtn.Clicked += async (_, _) => await RefreshAsync();
        _resetBtn.Clicked += async (_, _) => await ResetAsync();
        _dumpBtn.Clicked += async (_, _) => await DumpAsync();

        _selectAll = new CheckBox { VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center };
        _selectAll.CheckedChanged += (_, e) =>
        {
            if (_suppressSelectAll) return;
            foreach (var row in _rows)
                row.IsSelected = e.Value;
        };

        _filterKind = null;
        var allLabel = Loc.T("Tool.PortProxy.TabAll");
        _filterDrop = new PortProxyDropDown(_text, _muted, _line, _card)
        {
            Value = allLabel,
            WidthRequest = 120,
            MinimumWidthRequest = 108,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center
        };
        _filterDrop.SetOptions(allLabel, "v4tov4", "v4tov6", "v6tov4", "v6tov6");
        _filterDrop.SelectionChanged += OnFilterChanged;

        var adminBorder = new Border
        {
            Stroke = _line,
            BackgroundColor = _subtle,
            Padding = new Thickness(10, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = _admin
        };

        var toolbar = new Border
        {
            Stroke = _line,
            StrokeThickness = 1,
            BackgroundColor = _card,
            Padding = new Thickness(10, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                ],
                ColumnSpacing = 0,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    _addBtn,
                    Col(_deleteBtn, 1),
                    Col(_refreshBtn, 2),
                    Col(_resetBtn, 3),
                    Col(_dumpBtn, 4),
                    Col(_filterDrop, 6)
                }
            }
        };

        var header = BuildHeaderRow();
        _table = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.None,
            EmptyView = new Label
            {
                Text = Loc.T("Tool.PortProxy.Empty"),
                TextColor = _muted,
                FontSize = 13,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(12, 24)
            },
            ItemTemplate = new DataTemplate(() => BuildDataRow())
        };

        var tableHost = new Border
        {
            Stroke = _line,
            StrokeThickness = 1,
            BackgroundColor = _card,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(0, 0, 0, 2),
            Content = new Grid
            {
                RowDefinitions =
                [
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star)
                ],
                Children =
                {
                    header,
                    _table.Also(v => Grid.SetRow(v, 1))
                }
            }
        };

        var root = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            ],
            Padding = new Thickness(4, 0, 4, 4),
            RowSpacing = 8
        };
        root.Add(_hint, 0, 0);
        root.Add(adminBorder, 0, 1);
        root.Add(toolbar, 0, 2);
        root.Add(tableHost, 0, 3);
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

    private View BuildHeaderRow()
    {
        Label Header(string text) => new()
        {
            Text = text,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = _muted,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        };

        return new Border
        {
            BackgroundColor = _subtle,
            Padding = new Thickness(8, 10),
            StrokeThickness = 0,
            Content = new Grid
            {
                ColumnDefinitions = TableColumnDefinitions(),
                ColumnSpacing = 6,
                Children =
                {
                    _selectAll,
                    Header(Loc.T("Tool.PortProxy.ColKind")).Also(v => Grid.SetColumn(v, 1)),
                    Header(Loc.T("Tool.PortProxy.ColListenAddress")).Also(v => Grid.SetColumn(v, 2)),
                    Header(Loc.T("Tool.PortProxy.ColListenPort")).Also(v => Grid.SetColumn(v, 3)),
                    Header(Loc.T("Tool.PortProxy.ColConnectAddress")).Also(v => Grid.SetColumn(v, 4)),
                    Header(Loc.T("Tool.PortProxy.ColConnectPort")).Also(v => Grid.SetColumn(v, 5)),
                    Header(Loc.T("Tool.PortProxy.ColProtocol")).Also(v => Grid.SetColumn(v, 6)),
                    Header(Loc.T("Tool.PortProxy.ColActions")).Also(v => Grid.SetColumn(v, 7))
                }
            }
        };
    }

    private View BuildDataRow()
    {
        var check = new CheckBox { VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center };
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(PortProxyRuleRow.IsSelected), mode: BindingMode.TwoWay));
        check.CheckedChanged += (_, _) => SyncSelectAll();

        Label Cell(string prop)
        {
            var label = new Label
            {
                FontSize = 12,
                TextColor = _text,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            label.SetBinding(Label.TextProperty, prop);
            return label;
        }

        var edit = MakeRowBtn(Loc.T("Tool.PortProxy.Edit"));
        edit.Clicked += async (s, _) =>
        {
            if (s is BindableObject { BindingContext: PortProxyRuleRow row })
                await EditAsync(row.Rule);
        };

        var del = MakeRowBtn(Loc.T("Tool.PortProxy.Delete"), danger: true);
        del.Clicked += async (s, _) =>
        {
            if (s is BindableObject { BindingContext: PortProxyRuleRow row })
                await DeleteOneAsync(row.Rule);
        };

        var rowGrid = new Grid
        {
            ColumnDefinitions = TableColumnDefinitions(),
            ColumnSpacing = 6,
            Padding = new Thickness(8, 8),
            Children =
            {
                check,
                Cell(nameof(PortProxyRuleRow.KindToken)).Also(v => Grid.SetColumn(v, 1)),
                Cell(nameof(PortProxyRuleRow.ListenAddress)).Also(v => Grid.SetColumn(v, 2)),
                Cell(nameof(PortProxyRuleRow.ListenPort)).Also(v => Grid.SetColumn(v, 3)),
                Cell(nameof(PortProxyRuleRow.ConnectAddress)).Also(v => Grid.SetColumn(v, 4)),
                Cell(nameof(PortProxyRuleRow.ConnectPort)).Also(v => Grid.SetColumn(v, 5)),
                Cell(nameof(PortProxyRuleRow.Protocol)).Also(v => Grid.SetColumn(v, 6)),
                new HorizontalStackLayout
                {
                    Spacing = 6,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { edit, del }
                }.Also(v => Grid.SetColumn(v, 7))
            }
        };

        var border = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Content = rowGrid
        };

        var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
        tap.Tapped += async (_, _) =>
        {
            if (border.BindingContext is PortProxyRuleRow row)
                await EditAsync(row.Rule);
        };
        rowGrid.GestureRecognizers.Add(tap);

        var dbl = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        dbl.Tapped += async (_, _) =>
        {
            if (border.BindingContext is PortProxyRuleRow row)
                await EditAsync(row.Rule);
        };
        rowGrid.GestureRecognizers.Add(dbl);

        return border;
    }

    private Button MakeRowBtn(string text, bool danger = false)
    {
        var btn = new Button
        {
            Text = text,
            FontSize = 12,
            Padding = new Thickness(6, 0),
            WidthRequest = 52,
            HeightRequest = 28,
            MinimumHeightRequest = 28,
            MinimumWidthRequest = 0,
            BackgroundColor = Colors.Transparent,
            TextColor = danger ? _warn : _blue,
            BorderColor = Colors.Transparent,
            BorderWidth = 0,
            CornerRadius = 6
        };
        if (Application.Current?.Resources.TryGetValue("ProgIconBtn", out var style) == true
            && style is Style s)
            btn.Style = s;
        return btn;
    }

    private static Color ResolveColor(string key, string fallbackHex)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true)
        {
            if (value is Color c) return c;
            if (value is SolidColorBrush b) return b.Color;
        }

        return Color.FromArgb(fallbackHex);
    }

    private static Button MakeToolBarBtn(string text, Color bg, Color fg, Color? border = null, int marginRight = 8)
        => new()
        {
            Text = text,
            BackgroundColor = bg,
            TextColor = fg,
            BorderColor = border ?? bg,
            BorderWidth = border == null ? 0 : 1,
            Padding = new Thickness(12, 0),
            Margin = new Thickness(0, 0, marginRight, 0),
            FontSize = 13,
            HeightRequest = 32,
            MinimumHeightRequest = 32,
            MinimumWidthRequest = 72,
            CornerRadius = 8,
            VerticalOptions = LayoutOptions.Center
        };

    private static ColumnDefinitionCollection TableColumnDefinitions() =>
    [
        new ColumnDefinition(44),
        new ColumnDefinition(80),
        new ColumnDefinition(GridLength.Star),
        new ColumnDefinition(88),
        new ColumnDefinition(GridLength.Star),
        new ColumnDefinition(88),
        new ColumnDefinition(64),
        new ColumnDefinition(148)
    ];

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

    private static string RowKey(PortProxyRuleInfo rule)
        => $"{rule.KindToken}|{rule.ListenAddress}|{rule.ListenPort}|{rule.Protocol}";

    private static string RowKey(PortProxyRuleRow row) => RowKey(row.Rule);

    private void BindRules()
    {
        if (_service == null) return;
        var filter = CurrentFilter();
        var selectedKeys = _rows.Where(r => r.IsSelected).Select(RowKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        foreach (var rule in _service.Rules)
        {
            if (filter is not null && rule.Kind != filter.Value) continue;
            _rows.Add(new PortProxyRuleRow(rule)
            {
                IsSelected = selectedKeys.Contains(RowKey(rule))
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
            if (_rows.Count == 0)
            {
                _selectAll.IsChecked = false;
                return;
            }

            var selected = _rows.Count(r => r.IsSelected);
            _selectAll.IsChecked = selected > 0 && selected == _rows.Count;
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
        _filterDrop.SetEnabled(on);
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
            await PortProxyDumpPage.ShowAsync(GetNavigation(), text);
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

internal static class PortProxyViewExtensions
{
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}