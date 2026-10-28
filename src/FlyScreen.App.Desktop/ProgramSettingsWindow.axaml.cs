using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi;

public partial class ProgramSettingsWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly IReadOnlyList<ProgramSettingField> _fields;
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, Control> _editors = new(StringComparer.OrdinalIgnoreCase);
    private Control? _longPressPanel;

    public ProgramSettingsWindow() : this(new UserProgram { Name = "design" }) { }

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
            FieldsHost.Children.Add(new TextBlock
            {
                Text = Loc.T("Prog.SettingsNoSchema"),
                Foreground = Brushes.Gray
            });
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
                var boolGroup = new StackPanel { Spacing = 0, Margin = new Avalonia.Thickness(0) };
                while (i < _fields.Count && _fields[i].Kind == ProgramSettingKind.Bool)
                {
                    var bf = _fields[i];
                    _values.TryGetValue(bf.Key, out var bcur);
                    bcur ??= bf.DefaultRaw;
                    var row = new DockPanel { Margin = new Avalonia.Thickness(0), MinHeight = 26 };
                    var check = MakeCheck(bf.Key, ProgramSettingsSchema.AsBool(bcur));
                    DockPanel.SetDock(check, Dock.Right);
                    row.Children.Add(check);
                    row.Children.Add(new TextBlock
                    {
                        Text = bf.Label,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 13,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
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
                var panel = new StackPanel { Margin = new Avalonia.Thickness(0, 4, 0, 0) };
                panel.Children.Add(new TextBlock
                {
                    Text = field.Label,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Avalonia.Thickness(0, 8, 0, 2)
                });
                var sec = MakeText(field.Key, cur, false);
                _editors[field.Key] = sec;
                panel.Children.Add(sec);
                panel.IsVisible = IsLongPressActionSelected();
                _longPressPanel = panel;
                FieldsHost.Children.Add(panel);
                i++;
                continue;
            }

            FieldsHost.Children.Add(new TextBlock
            {
                Text = field.Label,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 8, 0, 2)
            });
            Control editor = field.Kind switch
            {
                ProgramSettingKind.StringList => MakeText(field.Key,
                    string.Join("\n", ProgramSettingsSchema.AsStringList(cur)), true),
                ProgramSettingKind.Enum => MakeCombo(field.Key, field.EnumOptions, cur),
                ProgramSettingKind.Double => MakeText(field.Key, cur, false),
                _ => MakeText(field.Key, cur, false)
            };
            _editors[field.Key] = editor;
            FieldsHost.Children.Add(editor);
            if (field.Kind == ProgramSettingKind.StringList)
            {
                FieldsHost.Children.Add(new TextBlock
                {
                    Text = "可换行；也可用分号分隔。保存时自动整理",
                    FontSize = 11,
                    Foreground = Brushes.Gray
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
                Margin = new Avalonia.Thickness(0, 10, 0, 4),
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });
            FieldsHost.Children.Add(BuildRegionTable());
            FieldsHost.Children.Add(new TextBlock
            {
                Text = Loc.T("Prog.CalibrateOnPhone"),
                Margin = new Avalonia.Thickness(0, 8, 0, 0),
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private Avalonia.Controls.Grid BuildRegionTable()
    {
        var g = new Avalonia.Controls.Grid { Margin = new Avalonia.Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(56)));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var r = 0; r < 3; r++)
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        void Cell(string text, int col, int row, bool header)
        {
            var border = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Avalonia.Thickness(1),
                Background = header ? Brush.Parse("#F3F4F6") : Brushes.White,
                Padding = new Avalonia.Thickness(6, 8),
                Margin = new Avalonia.Thickness(1),
                Child = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                    FontSize = 12
                }
            };
            Avalonia.Controls.Grid.SetColumn(border, col);
            Avalonia.Controls.Grid.SetRow(border, row);
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
        var c = new CheckBox
        {
            IsChecked = on,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        c.IsCheckedChanged += (_, _) => OnBoolChanged(key, c.IsChecked == true);
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

    private TextBox MakeText(string key, string text, bool multi)
    {
        var t = new TextBox
        {
            Text = text ?? "",
            AcceptsReturn = multi,
            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multi ? 80 : 28
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
            _longPressPanel.IsVisible = IsLongPressActionSelected();
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
            }
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Collect();
            ProgramSettingsStore.SaveValues(_catalog, _program, _values);
            Close(true);
        }
        catch (Exception ex)
        {
            var dlg = new Window
            {
                Title = Loc.T("Prog.ActSettings"),
                Width = 360,
                Height = 160,
                Content = new TextBlock { Text = ex.Message, Margin = new Avalonia.Thickness(16) }
            };
            await dlg.ShowDialog(this);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
