using System.Globalization;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;
#if ANDROID
using FlyScreen.AppMaui.Platforms.Android;
#endif

namespace FlyScreen.AppMaui;

/// <summary>按脚本 @fly-settings 生成的本程序设置页；可触发 Android 区域标定。</summary>
public sealed class ProgramSettingsPage : ContentPage
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly IReadOnlyList<ProgramSettingField> _fields;
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, View> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _enumTokens = new(StringComparer.OrdinalIgnoreCase);
    private View? _longPressRow;
    private readonly Dictionary<string, Label> _regionCellLabels = new(StringComparer.OrdinalIgnoreCase);

    public ProgramSettingsPage(UserProgram program)
    {
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        var source = _catalog.ReadSource(program);
        _fields = ProgramSettingsSchema.Parse(source);
        var disk = ProgramSettingsStore.LoadFile(ProgramSettingsStore.PathFor(_catalog, program));
        _values = ProgramSettingsStore.Resolve(_fields, disk);

        Title = $"{Loc.T("Prog.ActSettings")} — {program.Name}";
        BuildUi();
    }

    private void BuildUi()
    {
        var stack = new VerticalStackLayout
        {
            Padding = new Thickness(16, 12),
            Spacing = 8
        };

        if (_fields.Count == 0)
        {
            stack.Children.Add(new Label
            {
                Text = Loc.T("Prog.SettingsNoSchema"),
                TextColor = Colors.Gray
            });
        }
        else
        {
            // 连续 Bool 收成紧凑组，避免每个开关再吃一层 Spacing=8
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
                    var boolGroup = new VerticalStackLayout { Spacing = 0 };
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
                FontSize = 12,
                TextColor = Colors.Gray
            });
            stack.Children.Add(BuildRegionTable());
            RefreshRegionCells();
#if !ANDROID
            stack.Children.Add(new Label
            {
                Text = Loc.T("Prog.CalibrateOnPhone"),
                FontSize = 12,
                TextColor = Colors.Gray,
                Margin = new Thickness(0, 4, 0, 0)
            });
#endif
        }

        var saveBtn = new Button
        {
            Text = Loc.T("Prog.DlgSave"),
            BackgroundColor = Color.FromArgb("#059669"),
            TextColor = Colors.White,
            Margin = new Thickness(0, 8, 0, 0)
        };
        saveBtn.Clicked += async (_, _) => await SaveAsync();
        stack.Children.Add(saveBtn);

        Content = new ScrollView { Content = stack };
    }

    private View BuildFieldRow(ProgramSettingField field)
    {
        _values.TryGetValue(field.Key, out var cur);
        cur ??= field.DefaultRaw;

        if (field.Kind == ProgramSettingKind.Bool)
            return MakeSwitchRow(field.Key, field.Label, ProgramSettingsSchema.AsBool(cur));

        var box = new VerticalStackLayout { Spacing = 2 };
        box.Children.Add(new Label
        {
            Text = field.Label,
            FontAttributes = FontAttributes.Bold,
            FontSize = 14
        });

        View editor = field.Kind switch
        {
            ProgramSettingKind.StringList => MakeStringListEntry(field.Key, cur),
            ProgramSettingKind.Enum => MakePicker(field.Key, field.EnumOptions, cur),
            ProgramSettingKind.Double => MakeStepperEntry(field.Key, cur),
            _ => MakeStringListEntry(field.Key, cur)
        };
        _editors[field.Key] = editor;
        box.Children.Add(editor);

        if (field.Kind == ProgramSettingKind.StringList)
        {
            box.Children.Add(new Label
            {
                Text = "可换行；也可用分号分隔。保存时自动整理",
                FontSize = 11,
                TextColor = Colors.Gray
            });
        }

        return box;
    }

    /// <summary>标签与开关同一行；压缩 Switch 默认过高行高。</summary>
    private View MakeSwitchRow(string key, string label, bool on)
    {
        var sw = new Switch
        {
            IsToggled = on,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Scale = 0.85,
            HeightRequest = 28,
            MinimumHeightRequest = 28
        };
        sw.Toggled += (_, e) => OnBoolToggled(key, e.Value);
        _values[key] = on ? "true" : "false";
        _editors[key] = sw;

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Padding = new Thickness(0),
            HeightRequest = 32,
            MinimumHeightRequest = 32
        };
        grid.Add(new Label
        {
            Text = label,
            FontAttributes = FontAttributes.Bold,
            FontSize = 13,
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
                en.IsToggled = true; // 调试 OCR 依赖启用 OCR
        }

        if (IsKey(key, "enableOcr"))
            SyncOcrDebugDependency(enableOcr: value);

        _values[key] = value ? "true" : "false";
    }

    /// <summary>关闭启用OCR 时强制关掉调试OCR，并禁用其开关。</summary>
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

    /// <summary>多行可换行；分号/换行均可，保存时 Normalize 成统一换行列表。</summary>
    private View MakeStringListEntry(string key, string text)
    {
        var display = ToMultilineDisplay(text);
        var ed = new Editor
        {
            Text = display,
            FontSize = 14,
            AutoSize = EditorAutoSizeOption.TextChanges,
            Placeholder = "一行一项，或用分号分隔"
        };
        // 高度随内容，至少约 2 行，避免又裁成一行看不全
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

    private View MakePicker(string key, IReadOnlyList<string> options, string cur)
    {
        var list = options.Count > 0
            ? options.ToList()
            : ["SwipeUp", "SwipeDown", "SwipeLeft", "SwipeRight", "Tap", "DoubleTap", "LongPress"];
        _enumTokens[key] = list;
        var labels = list.Select(ActionLabel).ToList();

        // 兼容误存中文标签
        var idx = list.FindIndex(o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            idx = labels.FindIndex(l => string.Equals(l, cur, StringComparison.Ordinal));
        if (idx < 0) idx = 0;

        var pick = new Picker
        {
            Title = Loc.T("Prog.Action.SwipeUp").Contains("上滑") ? "选择操作" : "Action",
            ItemsSource = labels,
            SelectedIndex = idx,
            FontSize = 14,
            HorizontalOptions = LayoutOptions.Fill,
            MinimumHeightRequest = 44
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
            FontSize = 14
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

    private void CollectFromEditors()
    {
        foreach (var field in _fields)
        {
            if (field.Kind == ProgramSettingKind.Rect)
                continue;
            if (!_editors.TryGetValue(field.Key, out var view)) continue;
            switch (view)
            {
                case Switch sw:
                    _values[field.Key] = sw.IsToggled ? "true" : "false";
                    break;
                case Editor ed:
                    _values[field.Key] = ProgramSettingsSchema.NormalizeStored(field.Kind, ed.Text);
                    break;
                case Entry en:
                    _values[field.Key] = ProgramSettingsSchema.NormalizeStored(field.Kind, en.Text);
                    break;
                case Picker pick:
                    if (_enumTokens.TryGetValue(field.Key, out var tokens)
                        && pick.SelectedIndex >= 0
                        && pick.SelectedIndex < tokens.Count)
                        _values[field.Key] = tokens[pick.SelectedIndex];
                    break;
            }
        }
    }

    /// <summary>3 列：方向 / 识别区 / 操作区；2 行：竖屏 / 横屏。点单元格标定。</summary>
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

    private static View MakeHeaderCell(string text)
        => new Border
        {
            Stroke = Colors.LightGray,
            BackgroundColor = Color.FromArgb("#F3F4F6"),
            Padding = new Thickness(6, 8),
            StrokeThickness = 1,
            Content = new Label
            {
                Text = text,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
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
            Stroke = Color.FromArgb("#93C5FD"),
            BackgroundColor = Color.FromArgb("#EFF6FF"),
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
            label.TextColor = string.IsNullOrEmpty(fmt) ? Colors.Gray : Colors.Black;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            CollectFromEditors();
            ProgramSettingsStore.SaveValues(_catalog, _program, _values);
            await DisplayAlertAsync(Loc.T("Prog.ActSettings"), Loc.T("Prog.SettingsSaved"), "OK");
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Loc.T("Prog.ActSettings"), ex.Message, "OK");
        }
    }

    private async Task CalibrateAsync(string settingKey)
    {
        var title = ProgramRegionKeys.Caption(settingKey);
#if ANDROID
        // 方向提示期间也占会话，同一时间只能有一个标定
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
}
