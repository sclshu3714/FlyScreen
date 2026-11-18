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
        var source = _catalog.ReadSource(program);
        _fields = ProgramSettingsSchema.Parse(source);
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

        var box = new VerticalStackLayout { Spacing = 4 };
        box.Children.Add(FieldLabel(field.Label));

        View editor = field.Kind switch
        {
            ProgramSettingKind.Enum => MakePicker(field.Key, field.EnumOptions, cur),
            ProgramSettingKind.StringList => MakeStringListEntry(field.Key, cur),
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
