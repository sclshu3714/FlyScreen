using System.Collections.ObjectModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;
using FlyScreen.Ui.Maui;

namespace FlyScreen.AppMaui;

/// <summary>可视化维护 fly-settings.json：表格主界面 + 添加/编辑弹窗。</summary>
public sealed class ProgramFlySettingsDesignerPage : ContentPage
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ObservableCollection<FlySettingsFieldEdit> _items = [];
    private readonly MauiDialogShell _shell = new()
    {
        SizeMode = MauiDialogSize.Fill,
        ShowClose = true
    };
    private readonly TaskCompletionSource _closed = new();
    private readonly VerticalStackLayout _tableBody = new() { Spacing = 0 };
    private readonly CheckBox _headerCheck = new() { VerticalOptions = LayoutOptions.Center };
    private bool _headerSyncing;

    public Task WhenClosed => _closed.Task;

    public ProgramFlySettingsDesignerPage(UserProgram program)
    {
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        Title = Loc.T("Prog.FlySettingsTitle") + " — " + program.Name;
        Helpers.MauiThemeService.PrepareOverlayPage(this);
        _shell.Title = Title;
        _shell.CloseRequested += async (_, _) => await CloseAsync();

        foreach (var f in ProgramFlySettingsStore.LoadSchema(_catalog, _program))
            _items.Add(FlySettingsFieldEdit.FromField(f));

        _headerCheck.CheckedChanged += (_, e) =>
        {
            if (_headerSyncing) return;
            foreach (var item in _items)
                item.IsSelected = e.Value;
        };

        var add = MakePrimary(Loc.T("Prog.FlySettingsAdd"));
        add.Clicked += async (_, _) => await AddAsync();
        var del = MakeNormal(Loc.T("Prog.FlySettingsRemove"));
        del.Clicked += async (_, _) => await BatchRemoveAsync();

        var toolbar = new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { add, del }
        };

        var header = BuildHeaderRow();
        RebuildTable();

        // 表格占满中间区域；行数超出时区域内滚动
        var tableInner = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Star)
            },
            Children = { header }
        };
        var tableScroll = new ScrollView
        {
            VerticalOptions = LayoutOptions.Fill,
            Orientation = ScrollOrientation.Vertical,
            Content = _tableBody
        };
        Grid.SetRow(tableScroll, 1);
        tableInner.Children.Add(tableScroll);

        var tableBorder = new Border
        {
            Stroke = Color.FromArgb("#D1D5DB"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            BackgroundColor = Colors.Transparent,
            VerticalOptions = LayoutOptions.Fill,
            Content = tableInner
        };

        var body = new Grid
        {
            Padding = new Thickness(16, 12),
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Star)
            },
            RowSpacing = 10,
            VerticalOptions = LayoutOptions.Fill,
            Children = { toolbar }
        };
        Grid.SetRow(tableBorder, 1);
        body.Children.Add(tableBorder);
        _shell.Body = body;

        var cancel = MakeNormal(Loc.T("Prog.DlgCancel"), async (_, _) => await CloseAsync());
        var save = MakePrimary(Loc.T("Prog.DlgSave"));
        save.Clicked += async (_, _) => await SaveAsync();
        _shell.Footer = new HorizontalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = LayoutOptions.End,
            Children = { cancel, save }
        };
        Content = _shell;
    }

    private View BuildHeaderRow()
    {
        var grid = MakeRowGrid();
        grid.BackgroundColor = Color.FromArgb("#F3F4F6");
        grid.Padding = new Thickness(8, 8);
        grid.Add(_headerCheck);
        AddCell(grid, 1, Loc.T("Prog.FlySettingsColLabel"), bold: true);
        AddCell(grid, 2, Loc.T("Prog.FlySettingsColKey"), bold: true);
        AddCell(grid, 3, Loc.T("Prog.FlySettingsColKind"), bold: true);
        AddCell(grid, 4, Loc.T("Prog.FlySettingsColEvent"), bold: true);
        AddCell(grid, 5, Loc.T("Prog.FlySettingsColOps"), bold: true);
        return grid;
    }

    private void RebuildTable()
    {
        _tableBody.Children.Clear();
        for (var i = 0; i < _items.Count; i++)
            _tableBody.Children.Add(BuildDataRow(_items[i], i));
    }

    private View BuildDataRow(FlySettingsFieldEdit item, int index)
    {
        var grid = MakeRowGrid();
        grid.Padding = new Thickness(8, 6);
        if (index % 2 == 1)
            grid.BackgroundColor = Color.FromArgb("#F9FAFB");

        var cb = new CheckBox { VerticalOptions = LayoutOptions.Center };
        cb.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(FlySettingsFieldEdit.IsSelected), source: item));
        grid.Add(cb);

        AddCell(grid, 1, item.Label);
        AddCell(grid, 2, item.Key);
        AddCell(grid, 3, item.KindText);
        AddCell(grid, 4, item.EventDisplay);

        var ops = new HorizontalStackLayout
        {
            Spacing = 2,
            HorizontalOptions = LayoutOptions.End,
            Children =
            {
                MakeIconBtn("✎", Loc.T("Prog.ActEdit"), async () => await EditAsync(item)),
                MakeIconBtn("↑", "上移", () => MoveItem(item, -1)),
                MakeIconBtn("↓", "下移", () => MoveItem(item, +1)),
                MakeIconBtn("×", Loc.T("Prog.FlySettingsRemove"), async () => await DeleteOneAsync(item), danger: true)
            }
        };
        Grid.SetColumn(ops, 5);
        grid.Add(ops);
        return grid;
    }

    private static Grid MakeRowGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitionCollection
        {
            new(new GridLength(36)),
            new(new GridLength(1.4, GridUnitType.Star)),
            new(new GridLength(1, GridUnitType.Star)),
            new(new GridLength(0.8, GridUnitType.Star)),
            new(new GridLength(1, GridUnitType.Star)),
            new(new GridLength(140))
        },
        ColumnSpacing = 4
    };

    private static void AddCell(Grid grid, int col, string text, bool bold = false)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        };
        Grid.SetColumn(label, col);
        grid.Add(label);
    }

    private static Button MakeIconBtn(string text, string tip, Func<Task> action, bool danger = false)
    {
        var b = new Button
        {
            Text = text,
            WidthRequest = 32,
            HeightRequest = 32,
            Padding = new Thickness(0),
            FontSize = 14,
            BackgroundColor = Colors.Transparent,
            TextColor = danger ? Color.FromArgb("#EF4444") : Color.FromArgb("#2563EB")
        };
        ToolTipProperties.SetText(b, tip);
        b.Clicked += async (_, _) => await action();
        return b;
    }

    private static Button MakeIconBtn(string text, string tip, Action action, bool danger = false)
        => MakeIconBtn(text, tip, () => { action(); return Task.CompletedTask; }, danger);

    private void MoveItem(FlySettingsFieldEdit item, int delta)
    {
        var idx = _items.IndexOf(item);
        var next = idx + delta;
        if (idx < 0 || next < 0 || next >= _items.Count) return;
        _items.Move(idx, next);
        RebuildTable();
    }

    private async Task AddAsync()
    {
        var n = _items.Count + 1;
        var seed = new FlySettingsFieldEdit
        {
            Key = "field" + n,
            Label = Loc.T("Prog.FlySettingsNewField") + n,
            Kind = ProgramSettingKind.String
        };
        var page = new ProgramFlySettingsFieldPage(seed, isNew: true);
        page.BackgroundColor = Colors.Transparent;
        await Navigation.PushModalAsync(page, animated: true);
        var ok = await page.WhenClosed;
        if (!ok) return;
        _items.Add(page.Result);
        RebuildTable();
    }

    private async Task EditAsync(FlySettingsFieldEdit item)
    {
        var page = new ProgramFlySettingsFieldPage(item, isNew: false);
        page.BackgroundColor = Colors.Transparent;
        await Navigation.PushModalAsync(page, animated: true);
        var ok = await page.WhenClosed;
        if (!ok) return;
        item.CopyFrom(page.Result);
        RebuildTable();
    }

    private async Task DeleteOneAsync(FlySettingsFieldEdit item)
    {
        if (!await DisplayAlertAsync(Title, Loc.T("Prog.FlySettingsDeleteConfirm", item.Label),
                Loc.T("Prog.DlgOk"), Loc.T("Prog.DlgCancel")))
            return;
        _items.Remove(item);
        RebuildTable();
    }

    private async Task BatchRemoveAsync()
    {
        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            await DisplayAlertAsync(Title, Loc.T("Prog.FlySettingsNoSelection"), "OK");
            return;
        }
        if (!await DisplayAlertAsync(Title, Loc.T("Prog.FlySettingsBatchDeleteConfirm", selected.Count),
                Loc.T("Prog.DlgOk"), Loc.T("Prog.DlgCancel")))
            return;
        foreach (var item in selected)
            _items.Remove(item);
        _headerSyncing = true;
        _headerCheck.IsChecked = false;
        _headerSyncing = false;
        RebuildTable();
    }

    private async Task SaveAsync()
    {
        try
        {
            var fields = _items.Select(i => i.ToField()).ToList();
            foreach (var f in fields)
            {
                if (string.IsNullOrWhiteSpace(f.Key) || string.IsNullOrWhiteSpace(f.Label))
                    throw new InvalidOperationException(Loc.T("Prog.FlySettingsKeyLabelRequired"));
            }
            var keys = fields.Select(f => f.Key).ToList();
            if (keys.Count != keys.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                throw new InvalidOperationException(Loc.T("Prog.FlySettingsDupKey"));

            ProgramFlySettingsStore.SaveSchema(_catalog, _program, fields);
            await CloseAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Title, ex.Message, "OK");
        }
    }

    private async Task CloseAsync()
    {
        _closed.TrySetResult();
        try
        {
            if (Navigation.ModalStack.Count > 0)
                await Navigation.PopModalAsync();
        }
        catch { /* already closed */ }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }

    private static Button MakePrimary(string text)
    {
        var b = new Button { Text = text };
        if (TryStyle("DlgPrimaryBtn") is { } s) b.Style = s;
        return b;
    }

    private static Button MakeNormal(string text, EventHandler? click = null)
    {
        var b = new Button { Text = text };
        if (TryStyle("DlgNormalBtn") is { } s) b.Style = s;
        if (click != null) b.Clicked += click;
        return b;
    }

    private static Style? TryStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;
}
