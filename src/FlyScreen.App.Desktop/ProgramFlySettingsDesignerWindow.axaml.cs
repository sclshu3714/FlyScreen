using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi;

/// <summary>可视化维护 fly-settings.json：表格主界面 + 添加/编辑弹窗。</summary>
public partial class ProgramFlySettingsDesignerWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ObservableCollection<FlySettingsFieldEdit> _items = [];
    private readonly LinuxClientChrome? _chrome;
    private bool _saved;

    public ProgramFlySettingsDesignerWindow() : this(new UserProgram { Name = "—" }, null) { }

    public ProgramFlySettingsDesignerWindow(UserProgram program, Window? owner = null)
    {
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        InitializeComponent();
        DesktopThemeService.ApplyToChildWindow(this);
        MatchOwnerSize(owner);
        Loaded += (_, _) =>
        {
            DesktopThemeService.ApplyToChildWindow(this);
            MatchOwnerSize(owner);
        };

        var title = Loc.T("Prog.FlySettingsTitle") + " — " + program.Name;
        _chrome = LinuxClientChrome.Attach(
            this, ClientTitleBar, ClientTitleText, title,
            closeBtn: TitleCloseBtn, allowMaximize: true);
        Title = title;
        BtnCancel.Content = Loc.T("Prog.DlgCancel");
        BtnSave.Content = Loc.T("Prog.DlgSave");
        BtnAdd.Content = Loc.T("Prog.FlySettingsAdd");
        BtnRemove.Content = Loc.T("Prog.FlySettingsRemove");

        foreach (var f in ProgramFlySettingsStore.LoadSchema(_catalog, _program))
            _items.Add(FlySettingsFieldEdit.FromField(f));
        FieldGrid.ItemsSource = _items;
    }

    private void MatchOwnerSize(Window? owner)
    {
        if (owner == null) return;
        var w = owner.Bounds.Width > 40 ? owner.Bounds.Width : owner.Width;
        var h = owner.Bounds.Height > 40 ? owner.Bounds.Height : owner.Height;
        if (w > 40) Width = Math.Max(MinWidth, w);
        if (h > 40) Height = Math.Max(MinHeight, h);
        if (owner.WindowState == WindowState.Maximized)
            WindowState = WindowState.Maximized;
    }

    public bool Saved => _saved;

    private void HeaderCheck_Changed(object? sender, RoutedEventArgs e)
    {
        var on = HeaderCheck.IsChecked == true;
        foreach (var item in _items)
            item.IsSelected = on;
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        var n = _items.Count + 1;
        var seed = new FlySettingsFieldEdit
        {
            Key = "field" + n,
            Label = Loc.T("Prog.FlySettingsNewField") + n,
            Kind = ProgramSettingKind.String
        };
        var dlg = new ProgramFlySettingsFieldDialog(seed, isNew: true, this);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;
        _items.Add(dlg.Result);
    }

    private async void BatchRemove_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            await ShowAlertAsync(Loc.T("Prog.FlySettingsNoSelection"));
            return;
        }
        if (!await ConfirmAsync(Loc.T("Prog.FlySettingsBatchDeleteConfirm", selected.Count)))
            return;
        foreach (var item in selected)
            _items.Remove(item);
        HeaderCheck.IsChecked = false;
    }

    private async void RowEdit_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not FlySettingsFieldEdit item) return;
        var dlg = new ProgramFlySettingsFieldDialog(item, isNew: false, this);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;
        item.CopyFrom(dlg.Result);
    }

    private void RowUp_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not FlySettingsFieldEdit item) return;
        var idx = _items.IndexOf(item);
        if (idx <= 0) return;
        _items.Move(idx, idx - 1);
    }

    private void RowDown_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not FlySettingsFieldEdit item) return;
        var idx = _items.IndexOf(item);
        if (idx < 0 || idx >= _items.Count - 1) return;
        _items.Move(idx, idx + 1);
    }

    private async void RowDelete_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not FlySettingsFieldEdit item) return;
        if (!await ConfirmAsync(Loc.T("Prog.FlySettingsDeleteConfirm", item.Label)))
            return;
        _items.Remove(item);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
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
            _saved = true;
            Close();
        }
        catch (Exception ex)
        {
            await ShowAlertAsync(ex.Message);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private async Task ShowAlertAsync(string message)
    {
        var win = new Window
        {
            Title = Title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new Button { Content = "OK", Width = 88, Height = 32, HorizontalAlignment = HorizontalAlignment.Right }
                }
            }
        };
        if (win.Content is StackPanel sp && sp.Children[^1] is Button ok)
            ok.Click += (_, _) => win.Close();
        await win.ShowDialog(this);
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var win = new Window
        {
            Title = Title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var yes = new Button { Content = Loc.T("Prog.DlgOk"), Width = 88, Height = 32 };
        var no = new Button { Content = Loc.T("Prog.DlgCancel"), Width = 88, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        yes.Click += (_, _) => { tcs.TrySetResult(true); win.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };
        win.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { no, yes }
                }
            }
        };
        win.Closed += (_, _) => tcs.TrySetResult(false);
        await win.ShowDialog(this);
        return await tcs.Task;
    }
}
