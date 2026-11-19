using System.IO;
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
        _fields = ProgramFlySettingsStore.LoadSchema(_catalog, program);
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

            if (field.Kind == ProgramSettingKind.Button)
            {
                var btn = MakeSettingButton(field);
                FieldsHost.Children.Add(btn);
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
                ProgramSettingKind.Checks => MakeChecks(field.Key, field.EnumOptions, cur),
                ProgramSettingKind.Enum => MakeCombo(field.Key, field.EnumOptions, cur),
                ProgramSettingKind.Radio => MakeRadioGroup(field.Key, field.EnumOptions, cur),
                ProgramSettingKind.String => MakeText(field.Key, cur, 28),
                ProgramSettingKind.Int or ProgramSettingKind.Long or ProgramSettingKind.Double
                    => MakeText(field.Key, cur, 28),
                ProgramSettingKind.File => MakePathPicker(field, cur, folder: false),
                ProgramSettingKind.Folder => MakePathPicker(field, cur, folder: true),
                ProgramSettingKind.Table => MakeTable(field, cur),
                _ => MakeText(field.Key, cur, 28)
            };
            _editors[field.Key] = editor;
            FieldsHost.Children.Add(editor);
            if (field.Kind is ProgramSettingKind.StringList or ProgramSettingKind.Checks)
            {
                FieldsHost.Children.Add(new TextBlock
                {
                    Text = field.Kind == ProgramSettingKind.Checks
                        ? "可多选；保存为换行列表"
                        : "可换行；也可用分号分隔。保存时自动整理",
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

    private System.Windows.Controls.Button MakeSettingButton(ProgramSettingField field)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = field.Label,
            Height = 32,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(14, 0, 14, 0)
        };
        btn.Click += (_, _) =>
        {
            var ev = string.IsNullOrWhiteSpace(field.EventName) ? field.Key : field.EventName;
            ProgramSettingEventBus.Enqueue(_program.Id, ev, field.Key);
            MessageBox.Show(this,
                Loc.T("Prog.SettingsEventQueued", ev),
                Loc.T("Prog.ActSettings"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        };
        return btn;
    }

    private FrameworkElement MakePathPicker(ProgramSettingField field, string cur, bool folder)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        var browse = new System.Windows.Controls.Button
        {
            Content = "…", Width = 36, Height = 28, Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(browse, Dock.Right);
        var tb = new TextBox { Text = cur ?? "", Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
        tb.TextChanged += (_, _) => _values[field.Key] = tb.Text ?? "";
        browse.Click += (_, _) =>
        {
            if (folder)
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = field.Label,
                    UseDescriptionForTitle = true,
                    SelectedPath = Directory.Exists(tb.Text) ? tb.Text : ""
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    tb.Text = dlg.SelectedPath;
                    _values[field.Key] = dlg.SelectedPath;
                }
            }
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = field.Label,
                    Filter = ProgramSettingsSchema.FormatFileFilter(field.Filter),
                    FileName = File.Exists(tb.Text) ? tb.Text : ""
                };
                if (dlg.ShowDialog(this) == true)
                {
                    tb.Text = dlg.FileName;
                    _values[field.Key] = dlg.FileName;
                }
            }
        };
        row.Children.Add(browse);
        row.Children.Add(tb);
        _values[field.Key] = tb.Text ?? "";
        return row;
    }

    private FrameworkElement MakeRadioGroup(string key, IReadOnlyList<string> options, string cur)
    {
        var list = options.Count > 0 ? options.ToList() : ["A", "B"];
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Margin = new Thickness(0, 2, 0, 0) };
        var groupName = "rg_" + key;
        foreach (var opt in list)
        {
            var rb = new System.Windows.Controls.RadioButton
            {
                Content = opt,
                GroupName = groupName,
                IsChecked = string.Equals(opt, cur, StringComparison.OrdinalIgnoreCase),
                Margin = new Thickness(0, 2, 0, 2)
            };
            rb.Checked += (_, _) =>
            {
                if (rb.IsChecked == true)
                    _values[key] = opt;
            };
            panel.Children.Add(rb);
        }
        if (!_values.ContainsKey(key) && list.Count > 0)
            _values[key] = list.Any(o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase)) ? cur : list[0];
        return panel;
    }

    private FrameworkElement MakeChecks(string key, IReadOnlyList<string> options, string cur)
    {
        var selected = new HashSet<string>(ProgramSettingsSchema.AsStringList(cur), StringComparer.OrdinalIgnoreCase);
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        var boxes = new List<(string Opt, CheckBox Cb)>();
        void Sync()
        {
            _values[key] = string.Join("\n", boxes.Where(x => x.Cb.IsChecked == true).Select(x => x.Opt));
        }
        foreach (var opt in options)
        {
            var cb = new CheckBox
            {
                Content = opt,
                IsChecked = selected.Contains(opt),
                Margin = new Thickness(0, 2, 0, 2)
            };
            cb.Checked += (_, _) => Sync();
            cb.Unchecked += (_, _) => Sync();
            boxes.Add((opt, cb));
            panel.Children.Add(cb);
        }
        Sync();
        return panel;
    }

    private FrameworkElement MakeTable(ProgramSettingField field, string cur)
    {
        var spec = field.Table ?? new ProgramTableSpec { Cols = 1, Rows = 1 };
        var data = ProgramTableData.ParseOrEmpty(cur, spec);
        var rows = spec.Rows;
        var cols = spec.Cols;
        var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        var lead = spec.RowHeader ? 1 : 0;
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // spacer for row header col if any
        // rebuild properly
        g.ColumnDefinitions.Clear();
        if (spec.RowHeader)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        for (var c = 0; c < cols; c++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (spec.CellButton)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var r = 0; r < rows; r++)
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Header(string text, int col, int row)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tb, col);
            Grid.SetRow(tb, row);
            g.Children.Add(tb);
        }

        var colOffset = spec.RowHeader ? 1 : 0;
        if (spec.RowHeader)
            Header("", 0, 0);
        for (var c = 0; c < cols; c++)
        {
            var name = c < spec.ColNames.Count ? spec.ColNames[c] : "C" + (c + 1);
            Header(name, c + colOffset, 0);
        }
        if (spec.CellButton)
            Header("", cols + colOffset, 0);

        var cells = new TextBox[rows, cols];
        void Persist()
        {
            var next = ProgramTableData.CreateEmpty(rows, cols);
            for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                next.Cells[r][c] = cells[r, c]?.Text ?? "";
            _values[field.Key] = next.ToJson();
        }

        for (var r = 0; r < rows; r++)
        {
            if (spec.RowHeader)
            {
                var rn = r < spec.RowNames.Count ? spec.RowNames[r] : "R" + (r + 1);
                Header(rn, 0, r + 1);
            }
            for (var c = 0; c < cols; c++)
            {
                var tb = new TextBox
                {
                    Text = data.Cells[r][c],
                    Margin = new Thickness(2),
                    Height = 26,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                tb.TextChanged += (_, _) => Persist();
                cells[r, c] = tb;
                Grid.SetColumn(tb, c + colOffset);
                Grid.SetRow(tb, r + 1);
                g.Children.Add(tb);
            }
            if (spec.CellButton)
            {
                var rr = r;
                var btn = new System.Windows.Controls.Button
                {
                    Content = "▶",
                    Width = 32,
                    Height = 26,
                    Margin = new Thickness(2),
                    Tag = rr
                };
                btn.Click += (_, _) =>
                {
                    var ev = string.IsNullOrWhiteSpace(spec.ButtonEvent) ? field.Key : spec.ButtonEvent;
                    // detail: row (and optional focused col 0)
                    ProgramSettingEventBus.Enqueue(_program.Id, ev, $"{rr},0");
                    MessageBox.Show(this,
                        Loc.T("Prog.SettingsEventQueued", ev + $" ({rr},0)"),
                        Loc.T("Prog.ActSettings"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                };
                Grid.SetColumn(btn, cols + colOffset);
                Grid.SetRow(btn, r + 1);
                g.Children.Add(btn);
            }
        }
        Persist();
        _ = lead;
        return g;
    }

    private void Collect()
    {
        foreach (var field in _fields)
        {
            if (field.Kind is ProgramSettingKind.Rect or ProgramSettingKind.Button) continue;
            if (!_editors.TryGetValue(field.Key, out var el)) continue;
            switch (el)
            {
                case CheckBox cb:
                    _values[field.Key] = cb.IsChecked == true ? "true" : "false";
                    break;
                case TextBox tb:
                    _values[field.Key] = ProgramSettingsSchema.NormalizeStored(field.Kind, tb.Text, field);
                    break;
                case ComboBox:
                case StackPanel:
                case DockPanel:
                case Grid:
                    // 值已在交互时写入 _values
                    if (_values.TryGetValue(field.Key, out var raw))
                        _values[field.Key] = ProgramSettingsSchema.NormalizeStored(field.Kind, raw, field);
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
