using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;
using FlyScreen.Ui.Maui;

namespace FlyScreen.AppMaui;

/// <summary>添加 / 编辑单个 fly-settings 字段（弹层尺寸对齐 Wide 父窗）。</summary>
public sealed class ProgramFlySettingsFieldPage : ContentPage
{
    private readonly FlySettingsFieldEdit _draft;
    private readonly MauiDialogShell _shell = new()
    {
        SizeMode = MauiDialogSize.Fill,
        ShowClose = true
    };
    private readonly TaskCompletionSource<bool> _closed = new();
    private readonly Entry _edKey = MakeEntry();
    private readonly Entry _edLabel = MakeEntry();
    private readonly Picker _edKind = new() { HeightRequest = 36 };
    private readonly Entry _edDefault = MakeEntry();
    private readonly Label _lblDefault = FieldLabel("默认值");
    private readonly VerticalStackLayout _panelOptions = new() { Spacing = 4, IsVisible = false };
    private readonly Entry _edOptions = MakeEntry();
    private readonly VerticalStackLayout _panelFilter = new() { Spacing = 4, IsVisible = false };
    private readonly Entry _edFilter = MakeEntry();
    private readonly VerticalStackLayout _panelEvent = new() { Spacing = 4, IsVisible = false };
    private readonly Entry _edEvent = MakeEntry();
    private readonly VerticalStackLayout _panelTable = new() { Spacing = 4, IsVisible = false };
    private readonly Entry _edCols = MakeEntry();
    private readonly Entry _edRows = MakeEntry();
    private readonly Entry _edColNames = MakeEntry();
    private readonly Entry _edRowNames = MakeEntry();
    private readonly Switch _edRowHeader = new();
    private readonly Switch _edCellButton = new();
    private readonly Entry _edButtonEvent = MakeEntry();
    private readonly Label _txtKindDesc = new()
    {
        LineBreakMode = LineBreakMode.WordWrap,
        FontSize = 13
    };
    private readonly Label _txtKindExample = new()
    {
        LineBreakMode = LineBreakMode.WordWrap,
        FontSize = 12,
        FontFamily = "Consolas"
    };
    private bool _ok;
    private bool _loading;

    public FlySettingsFieldEdit Result => _draft;
    public Task<bool> WhenClosed => _closed.Task;

    public ProgramFlySettingsFieldPage(FlySettingsFieldEdit seed, bool isNew)
    {
        _draft = seed.Clone();
        Title = (isNew ? Loc.T("Prog.FlySettingsAdd") : Loc.T("Prog.ActEdit"))
                + " — " + Loc.T("Prog.FlySettingsTitle");
        Helpers.MauiThemeService.PrepareOverlayPage(this);
        _shell.Title = Title;
        _shell.CloseRequested += async (_, _) => await FinishAsync(false);

        _edKind.ItemsSource = Enum.GetValues<ProgramSettingKind>().Select(k => k.ToString()).ToList();
        _edKind.SelectedIndexChanged += (_, _) =>
        {
            if (_edKind.SelectedIndex < 0) return;
            ApplyKindVisibility(Enum.GetValues<ProgramSettingKind>()[_edKind.SelectedIndex]);
            RefreshHelp();
        };
        WireChanged(_edKey, _edLabel, _edDefault, _edOptions, _edFilter, _edEvent,
            _edCols, _edRows, _edColNames, _edRowNames, _edButtonEvent);
        _edRowHeader.Toggled += (_, _) => RefreshHelp();
        _edCellButton.Toggled += (_, _) => RefreshHelp();

        _panelOptions.Children.Add(FieldLabel("选项（逗号分隔）"));
        _panelOptions.Children.Add(_edOptions);
        _panelFilter.Children.Add(FieldLabel("文件过滤器"));
        _panelFilter.Children.Add(_edFilter);
        _panelEvent.Children.Add(FieldLabel("事件名 (event)"));
        _panelEvent.Children.Add(_edEvent);
        _panelTable.Children.Add(FieldLabel("列数"));
        _panelTable.Children.Add(_edCols);
        _panelTable.Children.Add(FieldLabel("行数"));
        _panelTable.Children.Add(_edRows);
        _panelTable.Children.Add(FieldLabel("列名（逗号分隔）"));
        _panelTable.Children.Add(_edColNames);
        _panelTable.Children.Add(FieldLabel("行名（逗号分隔）"));
        _panelTable.Children.Add(_edRowNames);
        _panelTable.Children.Add(MakeSwitchRow("第一列作为行头", _edRowHeader));
        _panelTable.Children.Add(MakeSwitchRow("单元格嵌入按钮", _edCellButton));
        _panelTable.Children.Add(FieldLabel("单元格按钮事件"));
        _panelTable.Children.Add(_edButtonEvent);

        LoadDraft();

        var leftCard = MakeCard(
            "字段属性",
            new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    FieldLabel("键 (key)"), _edKey,
                    FieldLabel("标签"), _edLabel,
                    FieldLabel("类型"), _edKind,
                    _lblDefault, _edDefault,
                    _panelOptions, _panelFilter, _panelEvent, _panelTable
                }
            });

        var rightCard = MakeCard(
            "类型说明与取值示例",
            new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    FieldLabel("类型说明"),
                    _txtKindDesc,
                    FieldLabel("取值示例"),
                    _txtKindExample
                }
            });

        _shell.Body = new Grid
        {
            Padding = new Thickness(20, 16, 20, 8),
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(new GridLength(12)),
                new(GridLength.Star)
            },
            Children =
            {
                WrapScroll(leftCard),
                Place(WrapScroll(rightCard), 2)
            }
        };

        var cancel = MakeNormal(Loc.T("Prog.DlgCancel"));
        cancel.Clicked += async (_, _) => await FinishAsync(false);
        var ok = MakePrimary(Loc.T("Prog.DlgOk"));
        ok.Clicked += async (_, _) =>
        {
            Commit();
            if (string.IsNullOrWhiteSpace(_draft.Key) || string.IsNullOrWhiteSpace(_draft.Label))
            {
                await DisplayAlertAsync(Title, Loc.T("Prog.FlySettingsKeyLabelRequired"), "OK");
                return;
            }
            await FinishAsync(true);
        };
        _shell.Footer = new HorizontalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = LayoutOptions.End,
            Children = { cancel, ok }
        };
        Content = _shell;
    }

    private void WireChanged(params Entry[] entries)
    {
        foreach (var e in entries)
            e.TextChanged += (_, _) => RefreshHelp();
    }

    private void LoadDraft()
    {
        _loading = true;
        _edKey.Text = _draft.Key;
        _edLabel.Text = _draft.Label;
        var kinds = Enum.GetValues<ProgramSettingKind>();
        _edKind.SelectedIndex = Array.IndexOf(kinds, _draft.Kind);
        _edDefault.Text = _draft.DefaultRaw;
        _edOptions.Text = _draft.EnumOptionsText;
        _edFilter.Text = _draft.Filter;
        _edEvent.Text = _draft.EventName;
        _edCols.Text = _draft.TableCols.ToString();
        _edRows.Text = _draft.TableRows.ToString();
        _edColNames.Text = _draft.ColNamesText;
        _edRowNames.Text = _draft.RowNamesText;
        _edRowHeader.IsToggled = _draft.RowHeader;
        _edCellButton.IsToggled = _draft.CellButton;
        _edButtonEvent.Text = _draft.ButtonEvent;
        ApplyKindVisibility(_draft.Kind);
        _loading = false;
        RefreshHelp();
    }

    private void ApplyKindVisibility(ProgramSettingKind kind)
    {
        _panelOptions.IsVisible = kind is ProgramSettingKind.Enum or ProgramSettingKind.Radio or ProgramSettingKind.Checks;
        _panelFilter.IsVisible = kind == ProgramSettingKind.File;
        _panelEvent.IsVisible = kind == ProgramSettingKind.Button;
        _panelTable.IsVisible = kind == ProgramSettingKind.Table;
        var showDefault = kind is not ProgramSettingKind.Button and not ProgramSettingKind.Table and not ProgramSettingKind.Rect;
        _lblDefault.IsVisible = showDefault;
        _edDefault.IsVisible = showDefault;
    }

    private void RefreshHelp()
    {
        if (_loading) return;
        SnapshotToDraft();
        var preview = FlySettingsFieldKindHelp.Build(_draft);
        _txtKindDesc.Text = preview.Description;
        _txtKindExample.Text = preview.Example;
    }

    private void SnapshotToDraft()
    {
        _draft.Key = _edKey.Text?.Trim() ?? "";
        _draft.Label = _edLabel.Text?.Trim() ?? "";
        if (_edKind.SelectedIndex >= 0)
            _draft.Kind = Enum.GetValues<ProgramSettingKind>()[_edKind.SelectedIndex];
        _draft.DefaultRaw = _edDefault.Text ?? "";
        _draft.EnumOptionsText = _edOptions.Text ?? "";
        _draft.Filter = _edFilter.Text ?? "";
        _draft.EventName = _edEvent.Text ?? "";
        if (int.TryParse(_edCols.Text, out var cols)) _draft.TableCols = cols;
        if (int.TryParse(_edRows.Text, out var rows)) _draft.TableRows = rows;
        _draft.ColNamesText = _edColNames.Text ?? "";
        _draft.RowNamesText = _edRowNames.Text ?? "";
        _draft.RowHeader = _edRowHeader.IsToggled;
        _draft.CellButton = _edCellButton.IsToggled;
        _draft.ButtonEvent = _edButtonEvent.Text ?? "";
    }

    private void Commit() => SnapshotToDraft();

    private async Task FinishAsync(bool ok)
    {
        _ok = ok;
        _closed.TrySetResult(ok);
        try
        {
            if (Navigation.ModalStack.Count > 0)
                await Navigation.PopModalAsync();
        }
        catch { /* already closed */ }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = FinishAsync(false);
        return true;
    }

    private static Entry MakeEntry() => new() { HeightRequest = 36 };

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Style = TryStyle("DlgSectionTitle"),
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static View MakeSwitchRow(string text, Switch sw)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto)
            }
        };
        grid.Add(new Label { Text = text, VerticalOptions = LayoutOptions.Center });
        Grid.SetColumn(sw, 1);
        grid.Add(sw);
        return grid;
    }

    private static Border MakeCard(string title, View body)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new Label
                {
                    Text = title,
                    FontAttributes = FontAttributes.Bold,
                    FontSize = 14,
                    TextColor = ColorOf("Muted")
                },
                body
            }
        };
        var border = new Border
        {
            Padding = new Thickness(14),
            StrokeThickness = 1,
            Stroke = ColorOf("ControlBorder") ?? Colors.LightGray,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            BackgroundColor = ColorOf("CardBg") ?? Colors.White,
            Content = stack
        };
        if (TryStyle("DlgCard") is { } s)
            border.Style = s;
        return border;
    }

    private static ScrollView WrapScroll(View content) => new()
    {
        Content = content,
        VerticalOptions = LayoutOptions.Fill
    };

    private static View Place(View view, int column)
    {
        Grid.SetColumn(view, column);
        return view;
    }

    private static Button MakePrimary(string text)
    {
        var b = new Button { Text = text };
        if (TryStyle("DlgPrimaryBtn") is { } s) b.Style = s;
        return b;
    }

    private static Button MakeNormal(string text)
    {
        var b = new Button { Text = text };
        if (TryStyle("DlgNormalBtn") is { } s) b.Style = s;
        return b;
    }

    private static Style? TryStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;

    private static Color? ColorOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : null;
}
