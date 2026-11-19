using System.Globalization;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;
using FlyScreen.Ui.Maui;
using Microsoft.Maui.Controls.Shapes;
#if ANDROID
using FlyScreen.AppMaui.Platforms.Android;
#endif

namespace FlyScreen.AppMaui;

/// <summary>按脚本 @fly-settings 生成的本程序设置页；统一 MauiDialogShell + 全局弹窗样式。</summary>
public sealed class ProgramSettingsPage : ContentPage
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly IReadOnlyList<ProgramSettingField> _fields;
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, View> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _enumTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Label> _regionCellLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly MauiDialogShell _shell = new()
    {
        SizeMode = MauiDialogSize.Form,
        ShowClose = true
    };
    private readonly TaskCompletionSource _closed = new();
    private View? _longPressRow;

    public Task WhenClosed => _closed.Task;

    public ProgramSettingsPage(UserProgram program)
    {
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _fields = ProgramFlySettingsStore.LoadSchema(_catalog, program);
        var disk = ProgramSettingsStore.LoadFile(ProgramSettingsStore.PathFor(_catalog, program));
        _values = ProgramSettingsStore.Resolve(_fields, disk);

        Title = $"{Loc.T("Prog.ActSettings")} — {program.Name}";
        Helpers.MauiThemeService.PrepareOverlayPage(this);
        _shell.Title = Title;
        _shell.CloseRequested += async (_, _) => await CloseAsync();
        BuildUi();
        Content = _shell;
    }

    private void BuildUi()
    {
        var stack = new VerticalStackLayout { Spacing = 10 };

        if (_fields.Count == 0)
        {
            stack.Children.Add(new Label
            {
                Text = Loc.T("Prog.SettingsNoSchema"),
                Style = TryStyle("DlgHint")
            });
        }
        else
        {
            for (var i = 0; i < _fields.Count;)
            {
                var field = _fields[i];
                if (field.Kind == ProgramSettingKind.Rect)
                {
                    i++;
                    continue;
                }

                if (field.Kind == ProgramSettingKind.Bool)
                {
                    // 勿 Spacing=0：Win Switch 会被挤扁裁切
                    var boolGroup = new VerticalStackLayout { Spacing = 6 };
                    while (i < _fields.Count && _fields[i].Kind == ProgramSettingKind.Bool)
                    {
                        boolGroup.Children.Add(BuildFieldRow(_fields[i]));
                        i++;
                    }
                    stack.Children.Add(boolGroup);
                    continue;
                }

                var row = BuildFieldRow(field);
                if (IsLongPressSecField(field))
                {
                    _longPressRow = row;
                    row.IsVisible = IsLongPressActionSelected();
                }
                stack.Children.Add(row);
                i++;
            }

            SyncOcrDebugDependency();
        }

        if (_fields.Any(f => f.Kind == ProgramSettingKind.Rect))
        {
            stack.Children.Add(new Label
            {
                Text = Loc.T("Prog.RegionTableHint"),
                Style = TryStyle("DlgHint"),
                Margin = new Thickness(0, 4, 0, 0)
            });
            stack.Children.Add(BuildRegionTable());
            RefreshRegionCells();
#if !ANDROID
            stack.Children.Add(new Label
            {
                Text = Loc.T("Prog.CalibrateOnPhone"),
                Style = TryStyle("DlgHint"),
                Margin = new Thickness(0, 4, 0, 0)
            });
#endif
        }

        _shell.Body = new ScrollView
        {
            Padding = new Thickness(20, 16, 20, 8),
            Content = stack
        };

        var cancel = MakeNormal(Loc.T("Prog.DlgCancel"));
        cancel.Clicked += async (_, _) => await CloseAsync();
        var save = MakePrimary(Loc.T("Prog.DlgSave"));
        save.Clicked += async (_, _) => await SaveAsync();
        _shell.Footer = new HorizontalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = LayoutOptions.End,
            Children = { cancel, save }
        };
    }

    private View BuildFieldRow(ProgramSettingField field)
    {
        _values.TryGetValue(field.Key, out var cur);
        cur ??= field.DefaultRaw;

        if (field.Kind == ProgramSettingKind.Bool)
            return MakeSwitchRow(field.Key, field.Label, ProgramSettingsSchema.AsBool(cur));

        if (field.Kind == ProgramSettingKind.Button)
            return MakeSettingButton(field);

        var box = new VerticalStackLayout { Spacing = 4 };
        box.Children.Add(FieldLabel(field.Label));

        View editor = field.Kind switch
        {
            ProgramSettingKind.Enum => MakePicker(field.Key, field.EnumOptions, cur),
            ProgramSettingKind.Radio => MakeRadioGroup(field.Key, field.EnumOptions, cur),
            ProgramSettingKind.Checks => MakeChecks(field.Key, field.EnumOptions, cur),
            ProgramSettingKind.StringList => MakeStringListEntry(field.Key, cur),
            ProgramSettingKind.String => MakeSingleLineEntry(field.Key, cur),
            ProgramSettingKind.Int or ProgramSettingKind.Long or ProgramSettingKind.Double
                => MakeStepperEntry(field.Key, cur),
            ProgramSettingKind.File => MakePathPicker(field, cur, folder: false),
            ProgramSettingKind.Folder => MakePathPicker(field, cur, folder: true),
            ProgramSettingKind.Table => MakeTable(field, cur),
            _ => MakeSingleLineEntry(field.Key, cur)
        };
        _editors[field.Key] = editor;
        box.Children.Add(editor);

        if (field.Kind is ProgramSettingKind.StringList or ProgramSettingKind.Checks)
        {
            box.Children.Add(new Label
            {
                Text = field.Kind == ProgramSettingKind.Checks
                    ? "可多选；保存为换行列表"
                    : "可换行；也可用分号分隔。保存时自动整理",
                Style = TryStyle("DlgHint"),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        return box;
    }

    private View MakeSwitchRow(string key, string label, bool on)
    {
        var sw = new Switch
        {
            IsToggled = on,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center
        };
        sw.Toggled += (_, e) => OnBoolToggled(key, e.Value);
        _values[key] = on ? "true" : "false";
        _editors[key] = sw;

        // Auto 行高：固定 HeightRequest + Scale 会在 Win 上裁切开关
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Padding = new Thickness(0, 2),
            MinimumHeightRequest = 40,
            VerticalOptions = LayoutOptions.Start
        };
        grid.Add(new Label
        {
            Text = label,
            Style = TryStyle("DlgSectionTitle"),
            Margin = new Thickness(0),
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        }, 0);
        grid.Add(sw, 1);
        return grid;
    }

    private void OnBoolToggled(string key, bool value)
    {
        if (IsKey(key, "debugOcr") && value)
        {
            if (_editors.TryGetValue("enableOcr", out var enView) && enView is Switch en && !en.IsToggled)
                en.IsToggled = true;
        }

        if (IsKey(key, "enableOcr"))
            SyncOcrDebugDependency(enableOcr: value);

        _values[key] = value ? "true" : "false";
    }

    private void SyncOcrDebugDependency(bool? enableOcr = null)
    {
        if (!_editors.TryGetValue("debugOcr", out var dbgView) || dbgView is not Switch debugSw)
            return;

        bool en;
        if (enableOcr.HasValue)
            en = enableOcr.Value;
        else if (_editors.TryGetValue("enableOcr", out var enView) && enView is Switch sw)
            en = sw.IsToggled;
        else
            en = ProgramSettingsSchema.AsBool(_values.GetValueOrDefault("enableOcr"), false);

        debugSw.IsEnabled = en;
        if (!en)
        {
            if (debugSw.IsToggled)
                debugSw.IsToggled = false;
            _values["debugOcr"] = "false";
        }
    }

    private static bool IsKey(string key, string expect)
        => string.Equals(key, expect, StringComparison.OrdinalIgnoreCase);

    private View MakeStringListEntry(string key, string text)
    {
        var display = ToMultilineDisplay(text);
        var ed = new Editor
        {
            Text = display,
            FontSize = 14,
            AutoSize = EditorAutoSizeOption.TextChanges,
            Placeholder = "一行一项，或用分号分隔",
            TextColor = ColorOf("Text") ?? Colors.Black,
            BackgroundColor = ColorOf("CardBg") ?? Colors.White
        };
        var lineCount = Math.Max(2, display.Split('\n').Length);
        ed.MinimumHeightRequest = Math.Min(160, 28 + lineCount * 22);
        ed.TextChanged += (_, e) =>
            _values[key] = ProgramSettingsSchema.NormalizeStored(ProgramSettingKind.StringList, e.NewTextValue);
        return ed;
    }

    private static string ToMultilineDisplay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return string.Join("\n", ProgramSettingsSchema.AsStringList(raw));
    }

    private Picker MakePicker(string key, IReadOnlyList<string> options, string cur)
    {
        var list = options.Count > 0
            ? options.ToList()
            : ["SwipeUp", "SwipeDown", "SwipeLeft", "SwipeRight", "Tap", "DoubleTap", "LongPress"];
        _enumTokens[key] = list;
        var labels = list.Select(ActionLabel).ToList();

        var idx = list.FindIndex(o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            idx = labels.FindIndex(l => string.Equals(l, cur, StringComparison.Ordinal));
        if (idx < 0) idx = 0;

        // 勿设 Picker.Title：Maui-Win 会多出一行标题并把下拉框高度压扁（与 ProjectionDialogPage 一致）
        var pick = new Picker
        {
            Title = "",
            ItemsSource = labels,
            SelectedIndex = idx,
            FontSize = 14,
            HeightRequest = 40,
            MinimumHeightRequest = 40,
            VerticalOptions = LayoutOptions.Start,
            HorizontalOptions = LayoutOptions.Fill,
            TextColor = ColorOf("Text") ?? Colors.Black,
            BackgroundColor = ColorOf("CardBg") ?? Colors.Transparent
        };
        pick.SelectedIndexChanged += (_, _) =>
        {
            if (pick.SelectedIndex >= 0 && pick.SelectedIndex < list.Count)
            {
                _values[key] = list[pick.SelectedIndex];
                if (IsActionFieldKey(key))
                    UpdateLongPressRowVisibility();
            }
        };
        _values[key] = list[idx];
        return pick;
    }

    private static bool IsActionFieldKey(string key)
        => string.Equals(key, "action", StringComparison.OrdinalIgnoreCase);

    private static bool IsLongPressSecField(ProgramSettingField field)
        => string.Equals(field.Key, "longPressSec", StringComparison.OrdinalIgnoreCase)
           || (field.Kind == ProgramSettingKind.Double
               && field.Label.Contains("长按", StringComparison.Ordinal));

    private bool IsLongPressActionSelected()
    {
        _values.TryGetValue("action", out var a);
        return string.Equals(a, nameof(ProgramHitAction.LongPress), StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateLongPressRowVisibility()
    {
        if (_longPressRow != null)
            _longPressRow.IsVisible = IsLongPressActionSelected();
    }

    private View MakeStepperEntry(string key, string cur)
    {
        var d = ProgramSettingsSchema.AsDouble(cur, 0.7);
        var entry = new Entry
        {
            Keyboard = Keyboard.Numeric,
            Text = d.ToString(CultureInfo.InvariantCulture),
            FontSize = 14,
            HeightRequest = 40,
            TextColor = ColorOf("Text") ?? Colors.Black,
            BackgroundColor = ColorOf("CardBg") ?? Colors.White
        };
        entry.TextChanged += (_, e) =>
        {
            if (double.TryParse(e.NewTextValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                _values[key] = v.ToString(CultureInfo.InvariantCulture);
        };
        return entry;
    }

    private static string ActionLabel(string token) => token switch
    {
        "SwipeUp" => Loc.T("Prog.Action.SwipeUp"),
        "SwipeDown" => Loc.T("Prog.Action.SwipeDown"),
        "SwipeLeft" => Loc.T("Prog.Action.SwipeLeft"),
        "SwipeRight" => Loc.T("Prog.Action.SwipeRight"),
        "Tap" => Loc.T("Prog.Action.Tap"),
        "DoubleTap" => Loc.T("Prog.Action.DoubleTap"),
        "LongPress" => Loc.T("Prog.Action.LongPress"),
        _ => token
    };

    private View MakeSettingButton(ProgramSettingField field)
    {
        var btn = MakePrimary(field.Label);
        btn.HorizontalOptions = LayoutOptions.Start;
        btn.Clicked += async (_, _) =>
        {
            var ev = string.IsNullOrWhiteSpace(field.EventName) ? field.Key : field.EventName;
            ProgramSettingEventBus.Enqueue(_program.Id, ev, field.Key);
            await DisplayAlertAsync(Title, Loc.T("Prog.SettingsEventQueued", ev), "OK");
        };
        return btn;
    }

    private View MakeSingleLineEntry(string key, string cur)
    {
        var entry = new Entry
        {
            Text = cur ?? "",
            FontSize = 14,
            HeightRequest = 40,
            TextColor = ColorOf("Text") ?? Colors.Black,
            BackgroundColor = ColorOf("CardBg") ?? Colors.White
        };
        entry.TextChanged += (_, e) => _values[key] = e.NewTextValue ?? "";
        _values[key] = entry.Text ?? "";
        return entry;
    }

    private View MakePathPicker(ProgramSettingField field, string cur, bool folder)
    {
        var entry = new Entry
        {
            Text = cur ?? "",
            FontSize = 14,
            HeightRequest = 40,
            HorizontalOptions = LayoutOptions.Fill,
            TextColor = ColorOf("Text") ?? Colors.Black,
            BackgroundColor = ColorOf("CardBg") ?? Colors.White
        };
        entry.TextChanged += (_, e) => _values[field.Key] = e.NewTextValue ?? "";
        var browse = MakeNormal("…");
        browse.WidthRequest = 44;
        browse.Clicked += async (_, _) =>
        {
            try
            {
                string? path = null;
                if (folder)
                {
#if WINDOWS
                    // unpackaged / 管理员下 MAUI 无可用 FolderPicker；用 Win32
                    path = await MainThread.InvokeOnMainThreadAsync(() =>
                        Platforms.Windows.Win32PathPicker.PickFolder(field.Label));
#elif ANDROID
                    path = await Platforms.Android.AndroidFolderMaterializer.PickAndMaterializeAsync()
                        .ConfigureAwait(true);
#else
                    path = await DisplayPromptAsync(
                        Loc.T("Prog.SettingsPickFolder"),
                        field.Label,
                        initialValue: entry.Text ?? "",
                        maxLength: 512);
#endif
                }
                else
                {
#if WINDOWS
                    // unpackaged / 管理员下 MAUI FilePicker 常 COMException，并弹出空「OK」框
                    var winFilter = Platforms.Windows.Win32PathPicker.ToNativeFilter(
                        ProgramSettingsSchema.FormatFileFilter(field.Filter));
                    path = await MainThread.InvokeOnMainThreadAsync(() =>
                        Platforms.Windows.Win32PathPicker.PickSingleFile(
                            title: field.Label,
                            filter: winFilter));
#else
                    var types = string.IsNullOrWhiteSpace(field.Filter)
                        ? null
                        : field.Filter.Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(p => p.TrimStart('*', '.'))
                            .Where(p => p.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    var pick = types is { Length: > 0 }
                        ? await FilePicker.Default.PickAsync(new PickOptions
                        {
                            PickerTitle = field.Label,
                            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                            {
                                { DevicePlatform.Android, types.Select(_ => "application/octet-stream") },
                                { DevicePlatform.iOS, types },
                                { DevicePlatform.MacCatalyst, types }
                            })
                        })
                        : await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = field.Label });
                    if (pick != null)
                    {
                        path = pick.FullPath;
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        {
                            await using var stream = await pick.OpenReadAsync();
                            path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), pick.FileName);
                            await using var fs = File.Create(path);
                            await stream.CopyToAsync(fs);
                        }
                    }
#endif
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    entry.Text = path.Trim();
                    _values[field.Key] = path.Trim();
                }
            }
            catch (Exception ex)
            {
                var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                await DisplayAlertAsync(Title, msg, "OK");
            }
        };
        _values[field.Key] = entry.Text ?? "";
        var pathGrid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 6
        };
        pathGrid.Add(entry, 0);
        pathGrid.Add(browse, 1);
        return pathGrid;
    }

    private View MakeRadioGroup(string key, IReadOnlyList<string> options, string cur)
    {
        var list = options.Count > 0 ? options.ToList() : ["A", "B"];
        var panel = new VerticalStackLayout { Spacing = 4 };
        foreach (var opt in list)
        {
            var rb = new RadioButton
            {
                Content = opt,
                GroupName = "rg_" + key,
                IsChecked = string.Equals(opt, cur, StringComparison.OrdinalIgnoreCase)
            };
            rb.CheckedChanged += (_, e) =>
            {
                if (e.Value)
                    _values[key] = opt;
            };
            panel.Children.Add(rb);
        }
        if (!_values.ContainsKey(key) && list.Count > 0)
            _values[key] = list.Any(o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase)) ? cur : list[0];
        return panel;
    }

    private View MakeChecks(string key, IReadOnlyList<string> options, string cur)
    {
        var selected = new HashSet<string>(ProgramSettingsSchema.AsStringList(cur), StringComparer.OrdinalIgnoreCase);
        var panel = new VerticalStackLayout { Spacing = 4 };
        var boxes = new List<(string Opt, CheckBox Cb)>();
        void Sync() => _values[key] = string.Join("\n", boxes.Where(x => x.Cb.IsChecked).Select(x => x.Opt));
        foreach (var opt in options)
        {
            var cb = new CheckBox { IsChecked = selected.Contains(opt) };
            var row = new HorizontalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    cb,
                    new Label { Text = opt, VerticalOptions = LayoutOptions.Center }
                }
            };
            cb.CheckedChanged += (_, _) => Sync();
            boxes.Add((opt, cb));
            panel.Children.Add(row);
        }
        Sync();
        return panel;
    }

    private View MakeTable(ProgramSettingField field, string cur)
    {
        var spec = field.Table ?? new ProgramTableSpec { Cols = 1, Rows = 1 };
        var data = ProgramTableData.ParseOrEmpty(cur, spec);
        var rows = spec.Rows;
        var cols = spec.Cols;
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        var colOffset = spec.RowHeader ? 1 : 0;
        if (spec.RowHeader)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(64)));
        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (spec.CellButton)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        void Header(string text, int col, int row)
        {
            var lab = new Label { Text = text, FontAttributes = FontAttributes.Bold, FontSize = 12 };
            grid.Add(lab, col, row);
        }
        if (spec.RowHeader) Header("", 0, 0);
        for (var c = 0; c < cols; c++)
            Header(c < spec.ColNames.Count ? spec.ColNames[c] : "C" + (c + 1), c + colOffset, 0);
        if (spec.CellButton) Header("", cols + colOffset, 0);

        var cells = new Entry[rows, cols];
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
                var en = new Entry
                {
                    Text = data.Cells[r][c],
                    FontSize = 13,
                    HeightRequest = 36,
                    TextColor = ColorOf("Text") ?? Colors.Black,
                    BackgroundColor = ColorOf("CardBg") ?? Colors.White
                };
                en.TextChanged += (_, _) => Persist();
                cells[r, c] = en;
                grid.Add(en, c + colOffset, r + 1);
            }
            if (spec.CellButton)
            {
                var rr = r;
                var btn = MakeNormal("▶");
                btn.WidthRequest = 40;
                btn.Clicked += async (_, _) =>
                {
                    var ev = string.IsNullOrWhiteSpace(spec.ButtonEvent) ? field.Key : spec.ButtonEvent;
                    ProgramSettingEventBus.Enqueue(_program.Id, ev, $"{rr},0");
                    await DisplayAlertAsync(Title, Loc.T("Prog.SettingsEventQueued", ev + $" ({rr},0)"), "OK");
                };
                grid.Add(btn, cols + colOffset, r + 1);
            }
        }
        Persist();
        return grid;
    }

    private void CollectFromEditors()
    {
        foreach (var field in _fields)
        {
            if (field.Kind is ProgramSettingKind.Rect or ProgramSettingKind.Button)
                continue;
            if (!_editors.TryGetValue(field.Key, out var view)) continue;
            switch (view)
            {
                case Switch sw:
                    _values[field.Key] = sw.IsToggled ? "true" : "false";
                    break;
                case Editor ed:
                    _values[field.Key] = ProgramSettingsSchema.NormalizeStored(field.Kind, ed.Text, field);
                    break;
                case Entry en:
                    _values[field.Key] = ProgramSettingsSchema.NormalizeStored(field.Kind, en.Text, field);
                    break;
                case Picker pick:
                    if (_enumTokens.TryGetValue(field.Key, out var tokens)
                        && pick.SelectedIndex >= 0
                        && pick.SelectedIndex < tokens.Count)
                        _values[field.Key] = tokens[pick.SelectedIndex];
                    break;
                default:
                    if (_values.TryGetValue(field.Key, out var raw))
                        _values[field.Key] = ProgramSettingsSchema.NormalizeStored(field.Kind, raw, field);
                    break;
            }
        }
    }

    private View BuildRegionTable()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(56)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            ColumnSpacing = 4,
            RowSpacing = 4
        };

        grid.Add(MakeHeaderCell(Loc.T("Prog.RegionOrient")), 0, 0);
        grid.Add(MakeHeaderCell(Loc.T("Prog.OcrRegion")), 1, 0);
        grid.Add(MakeHeaderCell(Loc.T("Prog.ActionRegion")), 2, 0);

        grid.Add(MakeHeaderCell(Loc.T("Prog.OrientPortrait")), 0, 1);
        grid.Add(MakeRegionCell(ProgramRegionKeys.OcrPortrait), 1, 1);
        grid.Add(MakeRegionCell(ProgramRegionKeys.ActionPortrait), 2, 1);

        grid.Add(MakeHeaderCell(Loc.T("Prog.OrientLandscape")), 0, 2);
        grid.Add(MakeRegionCell(ProgramRegionKeys.OcrLandscape), 1, 2);
        grid.Add(MakeRegionCell(ProgramRegionKeys.ActionLandscape), 2, 2);

        return grid;
    }

    private View MakeHeaderCell(string text)
        => new Border
        {
            Stroke = ColorOf("Line") ?? Colors.LightGray,
            BackgroundColor = ColorOf("SubtleBg") ?? Color.FromArgb("#F3F4F6"),
            Padding = new Thickness(6, 8),
            StrokeThickness = 1,
            Content = new Label
            {
                Text = text,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                TextColor = ColorOf("Text") ?? Colors.Black,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            }
        };

    private View MakeRegionCell(string settingKey)
    {
        var label = new Label
        {
            FontSize = 11,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 2,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center
        };
        _regionCellLabels[settingKey] = label;

        var border = new Border
        {
            Stroke = ColorOf("Blue") ?? Color.FromArgb("#93C5FD"),
            BackgroundColor = ColorOf("CardBg") ?? Color.FromArgb("#EFF6FF"),
            Padding = new Thickness(6, 10),
            StrokeThickness = 1,
            Content = label
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await CalibrateAsync(settingKey);
        border.GestureRecognizers.Add(tap);
        return border;
    }

    private void RefreshRegionCells()
    {
        foreach (var (key, label) in _regionCellLabels)
        {
            _values.TryGetValue(key, out var raw);
            var fmt = ProgramRegionKeys.FormatCell(raw);
            label.Text = string.IsNullOrEmpty(fmt)
                ? Loc.T("Prog.RegionTapCalibrate")
                : fmt;
            label.TextColor = string.IsNullOrEmpty(fmt)
                ? ColorOf("Muted") ?? Colors.Gray
                : ColorOf("Text") ?? Colors.Black;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            CollectFromEditors();
            ProgramSettingsStore.SaveValues(_catalog, _program, _values);
            await DisplayAlertAsync(Loc.T("Prog.ActSettings"), Loc.T("Prog.SettingsSaved"), "OK");
            await CloseAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Loc.T("Prog.ActSettings"), ex.Message, "OK");
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
        catch
        {
            /* 已关闭 */
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }

    private async Task CalibrateAsync(string settingKey)
    {
        var title = ProgramRegionKeys.Caption(settingKey);
#if ANDROID
        if (!FlyScreenRegionCalibrator.TryBeginSession())
        {
            await DisplayAlertAsync(title, Loc.T("Prog.CalibrateBusy"), "OK");
            return;
        }

        try
        {
            CollectFromEditors();
            ProgramSettingsStore.SaveValues(_catalog, _program, _values);
            var orientTip = ProgramRegionKeys.IsLandscapeKey(settingKey)
                ? "请先将设备转到横屏。"
                : "请先将设备转到竖屏。";
            await DisplayAlertAsync(
                title,
                orientTip + "请开启「显示在其他应用上层」。标定框会浮在其它 App 上，可切换到目标应用再拖动/缩放，完成后点确定。",
                "OK");

            var status = await FlyScreenRegionCalibrator.RunAsync(
                _catalog, _program, settingKey, sessionAlreadyHeld: true);
            if (status == RegionCalibrateStatus.Busy)
            {
                await DisplayAlertAsync(title, Loc.T("Prog.CalibrateBusy"), "OK");
                return;
            }

            if (status != RegionCalibrateStatus.Ok)
            {
                await DisplayAlertAsync(title, Loc.T("Prog.CalibrateFailed"), "OK");
                return;
            }

            var disk = ProgramSettingsStore.LoadFile(ProgramSettingsStore.PathFor(_catalog, _program));
            foreach (var (k, v) in disk.Values)
                _values[k] = v;
            ProgramRegionKeys.MigrateLegacy(_values);
            RefreshRegionCells();
            await DisplayAlertAsync(title, Loc.T("Prog.CalibrateDoneOne"), "OK");
        }
        catch
        {
            FlyScreenRegionCalibrator.CancelSession();
            throw;
        }
#else
        await DisplayAlertAsync(title, Loc.T("Prog.CalibrateOnPhone"), "OK");
#endif
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Style = TryStyle("DlgSectionTitle"),
        Margin = new Thickness(0, 4, 0, 0)
    };

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
