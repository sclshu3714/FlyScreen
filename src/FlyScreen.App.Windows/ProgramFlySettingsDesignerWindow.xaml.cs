using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen;

/// <summary>可视化维护 fly-settings.json：表格主界面 + 添加/编辑弹窗。</summary>
public partial class ProgramFlySettingsDesignerWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ObservableCollection<FlySettingsFieldEdit> _items = [];

    public ProgramFlySettingsDesignerWindow(UserProgram program, Window? owner = null)
    {
        InitializeComponent();
        ThemeService.ApplyToChildWindow(this);
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        Owner = owner;
        MatchOwnerSize(owner);
        Loaded += (_, _) => MatchOwnerSize(Owner);

        Title = Loc.T("Prog.FlySettingsTitle") + " — " + program.Name;
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
        var w = owner.ActualWidth > 40 ? owner.ActualWidth : owner.Width;
        var h = owner.ActualHeight > 40 ? owner.ActualHeight : owner.Height;
        if (w > 40) Width = Math.Max(MinWidth, w);
        if (h > 40) Height = Math.Max(MinHeight, h);
        Left = owner.Left;
        Top = owner.Top;
        if (owner.WindowState == WindowState.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void HeaderCheck_Click(object sender, RoutedEventArgs e)
    {
        var on = HeaderCheck.IsChecked == true;
        foreach (var item in _items)
            item.IsSelected = on;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var n = _items.Count + 1;
        var seed = new FlySettingsFieldEdit
        {
            Key = "field" + n,
            Label = Loc.T("Prog.FlySettingsNewField") + n,
            Kind = ProgramSettingKind.String
        };
        var dlg = new ProgramFlySettingsFieldDialog(seed, isNew: true, owner: this);
        if (dlg.ShowDialog() != true) return;
        _items.Add(dlg.Result);
    }

    private void BatchRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, Loc.T("Prog.FlySettingsNoSelection"), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, Loc.T("Prog.FlySettingsBatchDeleteConfirm", selected.Count), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        foreach (var item in selected)
            _items.Remove(item);
        HeaderCheck.IsChecked = false;
    }

    private void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FlySettingsFieldEdit item) return;
        var dlg = new ProgramFlySettingsFieldDialog(item, isNew: false, owner: this);
        if (dlg.ShowDialog() != true) return;
        item.CopyFrom(dlg.Result);
    }

    private void RowUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FlySettingsFieldEdit item) return;
        var idx = _items.IndexOf(item);
        if (idx <= 0) return;
        _items.Move(idx, idx - 1);
    }

    private void RowDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FlySettingsFieldEdit item) return;
        var idx = _items.IndexOf(item);
        if (idx < 0 || idx >= _items.Count - 1) return;
        _items.Move(idx, idx + 1);
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FlySettingsFieldEdit item) return;
        if (MessageBox.Show(this, Loc.T("Prog.FlySettingsDeleteConfirm", item.Label), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _items.Remove(item);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
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
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
