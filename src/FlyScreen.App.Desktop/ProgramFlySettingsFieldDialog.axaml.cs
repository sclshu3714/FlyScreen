using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi;

public partial class ProgramFlySettingsFieldDialog : Window
{
    private readonly FlySettingsFieldEdit _draft;
    private readonly LinuxClientChrome? _chrome;
    private bool _ok;
    private bool _loading;

    public FlySettingsFieldEdit Result => _draft;
    public bool Confirmed => _ok;

    public ProgramFlySettingsFieldDialog() : this(new FlySettingsFieldEdit(), true, null) { }

    public ProgramFlySettingsFieldDialog(FlySettingsFieldEdit seed, bool isNew, Window? owner)
    {
        _draft = seed.Clone();
        InitializeComponent();
        DesktopThemeService.ApplyToChildWindow(this);
        Loaded += (_, _) => DesktopThemeService.ApplyToChildWindow(this);

        if (owner != null)
        {
            Width = Math.Max(560, owner.Bounds.Width);
            Height = Math.Max(420, owner.Bounds.Height);
            if (owner.WindowState == WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }

        var title = (isNew ? Loc.T("Prog.FlySettingsAdd") : Loc.T("Prog.ActEdit"))
                    + " — " + Loc.T("Prog.FlySettingsTitle");
        _chrome = LinuxClientChrome.Attach(
            this, ClientTitleBar, ClientTitleText, title,
            closeBtn: TitleCloseBtn, allowMaximize: true);
        Title = title;
        BtnCancel.Content = Loc.T("Prog.DlgCancel");
        BtnOk.Content = Loc.T("Prog.DlgOk");

        EdKind.ItemsSource = Enum.GetValues<ProgramSettingKind>().ToList();
        LoadDraft();
    }

    private void LoadDraft()
    {
        _loading = true;
        EdKey.Text = _draft.Key;
        EdLabel.Text = _draft.Label;
        EdKind.SelectedItem = _draft.Kind;
        EdDefault.Text = _draft.DefaultRaw;
        EdOptions.Text = _draft.EnumOptionsText;
        EdFilter.Text = _draft.Filter;
        EdEvent.Text = _draft.EventName;
        EdCols.Text = _draft.TableCols.ToString();
        EdRows.Text = _draft.TableRows.ToString();
        EdColNames.Text = _draft.ColNamesText;
        EdRowNames.Text = _draft.RowNamesText;
        EdRowHeader.IsChecked = _draft.RowHeader;
        EdCellButton.IsChecked = _draft.CellButton;
        EdButtonEvent.Text = _draft.ButtonEvent;
        ApplyKindVisibility(_draft.Kind);
        _loading = false;
        RefreshHelp();
    }

    private void Kind_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (EdKind.SelectedItem is ProgramSettingKind kind)
            ApplyKindVisibility(kind);
        RefreshHelp();
    }

    private void Field_Changed(object? sender, TextChangedEventArgs e) => RefreshHelp();

    private void Field_Changed(object? sender, RoutedEventArgs e) => RefreshHelp();

    private void ApplyKindVisibility(ProgramSettingKind kind)
    {
        PanelOptions.IsVisible = kind is ProgramSettingKind.Enum or ProgramSettingKind.Radio or ProgramSettingKind.Checks;
        PanelFilter.IsVisible = kind == ProgramSettingKind.File;
        PanelEvent.IsVisible = kind == ProgramSettingKind.Button;
        PanelTable.IsVisible = kind == ProgramSettingKind.Table;
        var showDefault = kind is not ProgramSettingKind.Button and not ProgramSettingKind.Table and not ProgramSettingKind.Rect;
        LblDefault.IsVisible = showDefault;
        EdDefault.IsVisible = showDefault;
    }

    private void RefreshHelp()
    {
        if (_loading) return;
        SnapshotToDraft();
        var preview = FlySettingsFieldKindHelp.Build(_draft);
        TxtKindDesc.Text = preview.Description;
        TxtKindExample.Text = preview.Example;
    }

    private void SnapshotToDraft()
    {
        _draft.Key = EdKey.Text?.Trim() ?? "";
        _draft.Label = EdLabel.Text?.Trim() ?? "";
        if (EdKind.SelectedItem is ProgramSettingKind kind)
            _draft.Kind = kind;
        _draft.DefaultRaw = EdDefault.Text ?? "";
        _draft.EnumOptionsText = EdOptions.Text ?? "";
        _draft.Filter = EdFilter.Text ?? "";
        _draft.EventName = EdEvent.Text ?? "";
        if (int.TryParse(EdCols.Text, out var cols)) _draft.TableCols = cols;
        if (int.TryParse(EdRows.Text, out var rows)) _draft.TableRows = rows;
        _draft.ColNamesText = EdColNames.Text ?? "";
        _draft.RowNamesText = EdRowNames.Text ?? "";
        _draft.RowHeader = EdRowHeader.IsChecked == true;
        _draft.CellButton = EdCellButton.IsChecked == true;
        _draft.ButtonEvent = EdButtonEvent.Text ?? "";
    }

    private void Commit() => SnapshotToDraft();

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Commit();
        if (string.IsNullOrWhiteSpace(_draft.Key) || string.IsNullOrWhiteSpace(_draft.Label))
        {
            await ShowAlertAsync(Loc.T("Prog.FlySettingsKeyLabelRequired"));
            return;
        }
        _ok = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private async Task ShowAlertAsync(string message)
    {
        var win = new Window
        {
            Title = Title,
            Width = 420,
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
}
