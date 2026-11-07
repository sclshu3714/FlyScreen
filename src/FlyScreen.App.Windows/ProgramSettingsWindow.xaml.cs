using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Border = System.Windows.Controls.Border;
using Grid = System.Windows.Controls.Grid;
using Thickness = System.Windows.Thickness;

namespace FlyScreen;

public partial class ProgramSettingsWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly IReadOnlyList<ProgramSettingField> _fields;
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, FrameworkElement> _editors = new(StringComparer.OrdinalIgnoreCase);
    private UIElement? _longPressPanel;

    public ProgramSettingsWindow(UserProgram program)
    {
        InitializeComponent();
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _fields = ProgramSettingsSchema.Parse(_catalog.ReadSource(program));
        var disk = ProgramSettingsStore.LoadFile(ProgramSettingsStore.PathFor(_catalog, program));
        _values = ProgramSettingsStore.Resolve(_fields, disk);
        Title = $"{Loc.T("Prog.ActSettings")} — {program.Name}";
        BuildFields();
    }

    private void BuildFields()
    {
        FieldsHost.Children.Clear();
        if (_fields.Count == 0)
        {
            FieldsHost.Children.Add(new TextBlock { Text = Loc.T("Prog.SettingsNoSchema"), Foreground = System.Windows.Media.Brushes.Gray });
            return;
        }

        for (var i = 0; i < _fields.Count;)
        {
            var field = _fields[i];
            if (field.Kind == ProgramSettingKind.Rect)
            {
                i++;
                continue;
            }

            _values.TryGetValue(field.Key, out var cur);
            cur ??= field.DefaultRaw;

            if (field.Kind == ProgramSettingKind.Bool)
            {
                var boolGroup = new StackPanel { Margin = new Thickness(0) };
                while (i < _fields.Count && _fields[i].Kind == ProgramSettingKind.Bool)
                {
                    var bf = _fields[i];
                    _values.TryGetValue(bf.Key, out var bcur);
                    bcur ??= bf.DefaultRaw;
                    var row = new DockPanel { Margin = new Thickness(0), Height = 26 };
                    var check = MakeCheck(bf.Key, ProgramSettingsSchema.AsBool(bcur));
                    DockPanel.SetDock(check, Dock.Right);
                    row.Children.Add(check);
                    row.Children.Add(new TextBlock
                    {
                        Text = bf.Label,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 13
                    });
                    _editors[bf.Key] = check;
                    boolGroup.Children.Add(row);
                    i++;
                }
                FieldsHost.Children.Add(boolGroup);
                continue;
            }

            if (IsLongPressSecField(field))
            {
                var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                panel.Children.Add(new TextBlock
                {
                    Text = field.Label,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 2)
                });
                var sec = MakeText(field.Key, cur, 28);
                _editors[field.Key] = sec;
                panel.Children.Add(sec);
                panel.Visibility = IsLongPressActionSelected() ? Visibility.Visible : Visibility.Collapsed;
                _longPressPanel = panel;
                FieldsHost.Children.Add(panel);
                i++;
                continue;
            }

            FieldsHost.Children.Add(new TextBlock
            {
                Text = field.Label,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 2)
            });
            FrameworkElement editor = field.Kind switch
            {
                ProgramSettingKind.StringList => MakeText(field.Key,
                    string.Join("\n", ProgramSettingsSchema.AsStringList(cur)), 88),
                ProgramSettingKind.Enum => MakeCombo(field.Key, field.EnumOptions, cur),
                ProgramSettingKind.Double => MakeText(field.Key, cur, 28),
                _ => MakeText(field.Key, cur, 28)
            };
            _editors[field.Key] = editor;
            FieldsHost.Children.Add(editor);
            if (field.Kind == ProgramSettingKind.StringList)
            {
                FieldsHost.Children.Add(new TextBlock
                {
                    Text = "可换行；也可用分号分隔。保存时自动整理",
                    FontSize = 11,
                    Foreground = System.Windows.Media.Brushes.Gray
                });
            }
            i++;
        }

        SyncOcrDebugDependency();

        if (_fields.Any(f => f.Kind == ProgramSettingKind.Rect))
        {
            FieldsHost.Children.Add(new TextBlock
            {
                Text = Loc.T("Prog.RegionTableHint"),
                Margin = new Thickness(0, 10, 0, 4),
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });
            FieldsHost.Children.Add(BuildRegionTable());
            FieldsHost.Children.Add(new TextBlock
            {
                Text = Loc.T("Prog.CalibrateOnPhone"),
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private Grid BuildRegionTable()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < 3; r++)
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Cell(string text, int col, int row, bool header)
        {
            var border = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Background = header
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF4, 0xF6))
                    : System.Windows.Media.Brushes.White,
                Padding = new Thickness(6, 8, 6, 8),
                Margin = new Thickness(1),
                Child = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 12
                }
            };
            Grid.SetColumn(border, col);
            Grid.SetRow(border, row);
            g.Children.Add(border);
        }

        string Val(string key)
        {
            _values.TryGetValue(key, out var raw);
            var fmt = ProgramRegionKeys.FormatCell(raw);
            return string.IsNullOrEmpty(fmt) ? Loc.T("Prog.RegionUnset") : fmt;
        }

        Cell(Loc.T("Prog.RegionOrient"), 0, 0, true);
        Cell(Loc.T("Prog.OcrRegion"), 1, 0, true);
        Cell(Loc.T("Prog.ActionRegion"), 2, 0, true);
        Cell(Loc.T("Prog.OrientPortrait"), 0, 1, true);
        Cell(Val(ProgramRegionKeys.OcrPortrait), 1, 1, false);
        Cell(Val(ProgramRegionKeys.ActionPortrait), 2, 1, false);
        Cell(Loc.T("Prog.OrientLandscape"), 0, 2, true);
        Cell(Val(ProgramRegionKeys.OcrLandscape), 1, 2, false);
        Cell(Val(ProgramRegionKeys.ActionLandscape), 2, 2, false);
        return g;
    }

    private CheckBox MakeCheck(string key, bool on)
    {
        var c = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        c.Checked += (_, _) => OnBoolChanged(key, true);
        c.Unchecked += (_, _) => OnBoolChanged(key, false);
        return c;
    }

    private void OnBoolChanged(string key, bool value)
    {
        if (string.Equals(key, "debugOcr", StringComparison.OrdinalIgnoreCase) && value
            && _editors.TryGetValue("enableOcr", out var enEl) && enEl is CheckBox en && en.IsChecked != true)
            en.IsChecked = true;

        if (string.Equals(key, "enableOcr", StringComparison.OrdinalIgnoreCase))
            SyncOcrDebugDependency(value);

        _values[key] = value ? "true" : "false";
    }

    private void SyncOcrDebugDependency(bool? enableOcr = null)
    {
        if (!_editors.TryGetValue("debugOcr", out var dbgEl) || dbgEl is not CheckBox debugCb)
            return;

        bool en;
        if (enableOcr.HasValue)
            en = enableOcr.Value;
        else if (_editors.TryGetValue("enableOcr", out var enEl) && enEl is CheckBox enCb)
            en = enCb.IsChecked == true;
        else
            en = ProgramSettingsSchema.AsBool(_values.GetValueOrDefault("enableOcr"), false);

        debugCb.IsEnabled = en;
        if (!en)
        {
            if (debugCb.IsChecked == true)
                debugCb.IsChecked = false;
            _values["debugOcr"] = "false";
        }
    }

    private TextBox MakeText(string key, string text, double height)
    {
        var t = new TextBox
        {
            Text = text ?? "",
            AcceptsReturn = height > 40,
            TextWrapping = height > 40 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = height > 40 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Height = height,
            MinHeight = height
        };
        t.TextChanged += (_, _) => _values[key] = t.Text ?? "";
        return t;
    }

    private ComboBox MakeCombo(string key, IReadOnlyList<string> options, string cur)
    {
        var list = options.Count > 0
            ? options.ToList()
            : ["SwipeUp", "SwipeDown", "SwipeLeft", "SwipeRight", "Tap", "DoubleTap", "LongPress"];
        var combo = new ComboBox { ItemsSource = list };
        var idx = list.FindIndex(o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = idx >= 0 ? idx : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string s)
            {
                _values[key] = s;
                if (string.Equals(key, "action", StringComparison.OrdinalIgnoreCase))
                    UpdateLongPressVisibility();
            }
        };
        if (combo.SelectedItem is string sel)
            _values[key] = sel;
        return combo;
    }

    private static bool IsLongPressSecField(ProgramSettingField field)
        => string.Equals(field.Key, "longPressSec", StringComparison.OrdinalIgnoreCase)
           || (field.Kind == ProgramSettingKind.Double
               && field.Label.Contains("长按", StringComparison.Ordinal));

    private bool IsLongPressActionSelected()
    {
        _values.TryGetValue("action", out var a);
        return string.Equals(a, nameof(ProgramHitAction.LongPress), StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateLongPressVisibility()
    {
        if (_longPressPanel != null)
            _longPressPanel.Visibility = IsLongPressActionSelected() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Collect()
    {
        foreach (var field in _fields)
        {
            if (field.Kind == ProgramSettingKind.Rect) continue;
            if (!_editors.TryGetValue(field.Key, out var el)) continue;
            switch (el)
            {
                case CheckBox cb:
                    _values[field.Key] = cb.IsChecked == true ? "true" : "false";
                    break;
                case TextBox tb:
                    _values[field.Key] = ProgramSettingsSchema.NormalizeStored(field.Kind, tb.Text);
                    break;
                case ComboBox:
                    break;
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Collect();
            ProgramSettingsStore.SaveValues(_catalog, _program, _values);
            MessageBox.Show(this, Loc.T("Prog.SettingsSaved"), Loc.T("Prog.ActSettings"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Prog.ActSettings"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
