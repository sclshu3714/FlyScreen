using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FlyScreen.DesktopUi.Helpers;
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
        DesktopThemeService.ApplyToChildWindow(this);
        Loaded += (_, _) => DesktopThemeService.ApplyToChildWindow(this);
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

            if (field.Kind == ProgramSettingKind.Button)
            {
                FieldsHost.Children.Add(MakeSettingButton(field));
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
                ProgramSettingKind.Checks => MakeChecks(field.Key, field.EnumOptions, cur),
                ProgramSettingKind.Enum => MakeCombo(field.Key, field.EnumOptions, cur),
                ProgramSettingKind.Radio => MakeRadioGroup(field.Key, field.EnumOptions, cur),
                ProgramSettingKind.String => MakeText(field.Key, cur, false),
                ProgramSettingKind.Int or ProgramSettingKind.Long or ProgramSettingKind.Double
                    => MakeText(field.Key, cur, false),
                ProgramSettingKind.File => MakePathPicker(field, cur, folder: false),
                ProgramSettingKind.Folder => MakePathPicker(field, cur, folder: true),
                ProgramSettingKind.Table => MakeTable(field, cur),
                _ => MakeText(field.Key, cur, false)
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

    private Button MakeSettingButton(ProgramSettingField field)
    {
        var btn = new Button
        {
            Content = field.Label,
            Height = 32,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Avalonia.Thickness(14, 0)
        };
        btn.Click += async (_, _) =>
        {
            var ev = string.IsNullOrWhiteSpace(field.EventName) ? field.Key : field.EventName;
            ProgramSettingEventBus.Enqueue(_program.Id, ev, field.Key);
            await ShowInfoAsync(Loc.T("Prog.SettingsEventQueued", ev));
        };
        return btn;
    }

    private Control MakePathPicker(ProgramSettingField field, string cur, bool folder)
    {
        var tb = new TextBox { Text = cur ?? "", MinHeight = 28 };
        tb.TextChanged += (_, _) => _values[field.Key] = tb.Text ?? "";
        var browse = new Button { Content = "…", Width = 36, Height = 28, Margin = new Avalonia.Thickness(6, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            if (folder)
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = field.Label,
                    AllowMultiple = false
                });
                var path = folders.FirstOrDefault()?.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                {
                    tb.Text = path;
                    _values[field.Key] = path;
                }
            }
            else
            {
                var patterns = string.IsNullOrWhiteSpace(field.Filter)
                    ? ["*.*"]
                    : field.Filter.Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = field.Label,
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Files") { Patterns = patterns.ToList() }
                    ]
                });
                var path = files.FirstOrDefault()?.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                {
                    tb.Text = path;
                    _values[field.Key] = path;
                }
            }
        };
        _values[field.Key] = tb.Text ?? "";
        DockPanel.SetDock(browse, Dock.Right);
        return new DockPanel { Children = { browse, tb } };
    }

    private Control MakeRadioGroup(string key, IReadOnlyList<string> options, string cur)
    {
        var list = options.Count > 0 ? options.ToList() : ["A", "B"];
        var panel = new StackPanel { Spacing = 4 };
        foreach (var opt in list)
        {
            var rb = new RadioButton
            {
                Content = opt,
                GroupName = "rg_" + key,
                IsChecked = string.Equals(opt, cur, StringComparison.OrdinalIgnoreCase)
            };
            rb.IsCheckedChanged += (_, _) =>
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

    private Control MakeChecks(string key, IReadOnlyList<string> options, string cur)
    {
        var selected = new HashSet<string>(ProgramSettingsSchema.AsStringList(cur), StringComparer.OrdinalIgnoreCase);
        var panel = new StackPanel { Spacing = 4 };
        var boxes = new List<(string Opt, CheckBox Cb)>();
        void Sync() => _values[key] = string.Join("\n", boxes.Where(x => x.Cb.IsChecked == true).Select(x => x.Opt));
        foreach (var opt in options)
        {
            var cb = new CheckBox { Content = opt, IsChecked = selected.Contains(opt) };
            cb.IsCheckedChanged += (_, _) => Sync();
            boxes.Add((opt, cb));
            panel.Children.Add(cb);
        }
        Sync();
        return panel;
    }

    private Control MakeTable(ProgramSettingField field, string cur)
    {
        var spec = field.Table ?? new ProgramTableSpec { Cols = 1, Rows = 1 };
        var data = ProgramTableData.ParseOrEmpty(cur, spec);
        var rows = spec.Rows;
        var cols = spec.Cols;
        var g = new Avalonia.Controls.Grid();
        if (spec.RowHeader)
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(72)));
        for (var c = 0; c < cols; c++)
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (spec.CellButton)
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var r = 0; r < rows; r++)
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var colOffset = spec.RowHeader ? 1 : 0;
        void Header(string text, int col, int row)
        {
            var tb = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Avalonia.Thickness(4) };
            Avalonia.Controls.Grid.SetColumn(tb, col);
            Avalonia.Controls.Grid.SetRow(tb, row);
            g.Children.Add(tb);
        }
        if (spec.RowHeader) Header("", 0, 0);
        for (var c = 0; c < cols; c++)
            Header(c < spec.ColNames.Count ? spec.ColNames[c] : "C" + (c + 1), c + colOffset, 0);
        if (spec.CellButton) Header("", cols + colOffset, 0);

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
                Header(r < spec.RowNames.Count ? spec.RowNames[r] : "R" + (r + 1), 0, r + 1);
            for (var c = 0; c < cols; c++)
            {
                var tb = new TextBox { Text = data.Cells[r][c], Margin = new Avalonia.Thickness(2), MinHeight = 26 };
                tb.TextChanged += (_, _) => Persist();
                cells[r, c] = tb;
                Avalonia.Controls.Grid.SetColumn(tb, c + colOffset);
                Avalonia.Controls.Grid.SetRow(tb, r + 1);
                g.Children.Add(tb);
            }
            if (spec.CellButton)
            {
                var rr = r;
                var btn = new Button { Content = "▶", Width = 32, Height = 26, Margin = new Avalonia.Thickness(2) };
                btn.Click += async (_, _) =>
                {
                    var ev = string.IsNullOrWhiteSpace(spec.ButtonEvent) ? field.Key : spec.ButtonEvent;
                    ProgramSettingEventBus.Enqueue(_program.Id, ev, $"{rr},0");
                    await ShowInfoAsync(Loc.T("Prog.SettingsEventQueued", ev + $" ({rr},0)"));
                };
                Avalonia.Controls.Grid.SetColumn(btn, cols + colOffset);
                Avalonia.Controls.Grid.SetRow(btn, r + 1);
                g.Children.Add(btn);
            }
        }
        Persist();
        return g;
    }

    private async Task ShowInfoAsync(string message)
    {
        var win = new Window
        {
            Title = Loc.T("Prog.ActSettings"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new Button { Content = "OK", Width = 88, HorizontalAlignment = HorizontalAlignment.Right }
                }
            }
        };
        if (win.Content is StackPanel sp && sp.Children[^1] is Button ok)
            ok.Click += (_, _) => win.Close();
        await win.ShowDialog(this);
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
                default:
                    if (_values.TryGetValue(field.Key, out var raw))
                        _values[field.Key] = ProgramSettingsSchema.NormalizeStored(field.Kind, raw, field);
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
