using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;
using TextBox = System.Windows.Controls.TextBox;

namespace FlyScreen;

/// <summary>添加 / 编辑单个 fly-settings 字段。</summary>
public partial class ProgramFlySettingsFieldDialog : Window
{
    private readonly FlySettingsFieldEdit _draft;
    private bool _loading;

    public FlySettingsFieldEdit Result => _draft;

    public ProgramFlySettingsFieldDialog(FlySettingsFieldEdit seed, bool isNew, Window? owner = null)
    {
        InitializeComponent();
        ThemeService.ApplyToChildWindow(this);
        Owner = owner;
        if (owner != null)
        {
            Width = Math.Max(560, owner.ActualWidth);
            Height = Math.Max(420, owner.ActualHeight);
            if (owner.WindowState == WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }

        _draft = seed.Clone();
        Title = (isNew ? Loc.T("Prog.FlySettingsAdd") : Loc.T("Prog.ActEdit"))
                + " — " + Loc.T("Prog.FlySettingsTitle");
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

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EdKind.SelectedItem is ProgramSettingKind kind)
            ApplyKindVisibility(kind);
        RefreshHelp();
    }

    private void Field_Changed(object sender, RoutedEventArgs e) => RefreshHelp();

    private void ApplyKindVisibility(ProgramSettingKind kind)
    {
        PanelOptions.Visibility = kind is ProgramSettingKind.Enum or ProgramSettingKind.Radio or ProgramSettingKind.Checks
            ? Visibility.Visible : Visibility.Collapsed;
        PanelFilter.Visibility = kind == ProgramSettingKind.File ? Visibility.Visible : Visibility.Collapsed;
        PanelEvent.Visibility = kind == ProgramSettingKind.Button ? Visibility.Visible : Visibility.Collapsed;
        PanelTable.Visibility = kind == ProgramSettingKind.Table ? Visibility.Visible : Visibility.Collapsed;
        var showDefault = kind is not ProgramSettingKind.Button and not ProgramSettingKind.Table and not ProgramSettingKind.Rect;
        LblDefault.Visibility = showDefault ? Visibility.Visible : Visibility.Collapsed;
        EdDefault.Visibility = showDefault ? Visibility.Visible : Visibility.Collapsed;
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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (string.IsNullOrWhiteSpace(_draft.Key) || string.IsNullOrWhiteSpace(_draft.Label))
        {
            MessageBox.Show(this, Loc.T("Prog.FlySettingsKeyLabelRequired"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
