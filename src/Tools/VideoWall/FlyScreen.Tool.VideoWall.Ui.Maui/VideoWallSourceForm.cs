using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Ui.Maui;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>按 <see cref="SourceFormSchema"/> 渲染添加/编辑源表单（Maui）。</summary>
internal sealed class VideoWallSourceForm : VerticalStackLayout
{
    private readonly VideoWallFlyoutHost _flyout;
    private readonly Func<IReadOnlyList<string>> _groupNames;
    private readonly Color _text;
    private readonly Color _muted;
    private readonly Color _line;
    private readonly Color _card;
    private readonly VerticalStackLayout _fieldsHost = new() { Spacing = 8 };
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _pathEntries = new(StringComparer.OrdinalIgnoreCase);
    private Entry? _nameEntry;
    private VideoSourceKind _kind = VideoSourceKind.Camera;
    private bool _kindLocked;

    public VideoSourceKind Kind => _kind;

    public VideoWallSourceForm(
        VideoWallFlyoutHost flyout,
        Func<IReadOnlyList<string>> groupNames,
        Color text,
        Color muted,
        Color line,
        Color card)
    {
        _flyout = flyout;
        _groupNames = groupNames;
        _text = text;
        _muted = muted;
        _line = line;
        _card = card;
        Spacing = 10;
        Children.Add(_fieldsHost);
    }

    public void BeginAdd(VideoSourceKind defaultKind = VideoSourceKind.Camera)
    {
        _kindLocked = false;
        _kind = defaultKind;
        _values.Clear();
        _pathEntries.Clear();
        Rebuild();
    }

    public void BeginEdit(VideoSourceDescriptor src, CredentialStore? credentials = null)
    {
        _kindLocked = true;
        _kind = src.Kind;
        _values.Clear();
        _pathEntries.Clear();
        _values["name"] = src.Name;
        _values["address"] = src.Endpoint.Address;
        if (src.Kind == VideoSourceKind.LocalFile)
            _values["path"] = src.Endpoint.Address;
        _values["groupId"] = src.GroupId;
        _values["decode"] = src.DecodePreference;
        _values["note"] = src.Note;
        if (src.Endpoint.BufferMs is int buf)
            _values["bufferMs"] = buf.ToString();
        if (!string.IsNullOrWhiteSpace(src.Endpoint.Transport))
            _values["transport"] = src.Endpoint.Transport;
        if (credentials is not null && !string.IsNullOrWhiteSpace(src.Endpoint.CredentialRef))
        {
            var cred = credentials.Get(src.Endpoint.CredentialRef);
            if (cred is not null)
            {
                _values["credentialRef"] = src.Endpoint.CredentialRef;
                _values["auth"] = string.IsNullOrWhiteSpace(cred.AuthKind)
                    ? SourceFormSchema.AuthBasic
                    : cred.AuthKind;
                _values["username"] = cred.Username;
                _values["password"] = cred.Password;
            }
        }

        Rebuild();
    }

    public IReadOnlyDictionary<string, string?> Values => _values;

    public void SetKind(VideoSourceKind kind)
    {
        if (_kindLocked || _kind == kind) return;
        _kind = kind;
        Rebuild();
    }

    private void Rebuild()
    {
        _flyout.Close();
        _fieldsHost.Children.Clear();
        _pathEntries.Clear();
        _nameEntry = null;

        if (_kindLocked)
        {
            _fieldsHost.Children.Add(new Label
            {
                Text = KindLabel(_kind),
                FontSize = 13,
                TextColor = _muted
            });
        }
        else
        {
            var typeDrop = new VideoWallDropDown(_text, _muted, _line, _card);
            var specs = SourceFormSchema.Types
                .Where(t => t.Implemented && t.Kind != VideoSourceKind.NvrChannel)
                .ToList();
            typeDrop.SetOptions(specs.Select(s => Loc.T(s.LabelKey)).ToArray());
            var idx = Math.Max(0, specs.FindIndex(s => s.Kind == _kind));
            typeDrop.SelectedIndex = idx;
            typeDrop.BindFlyout(_flyout);
            typeDrop.SelectionChanged += _ =>
            {
                var i = typeDrop.SelectedIndex;
                if (i >= 0 && i < specs.Count)
                    SetKind(specs[i].Kind);
            };
            _fieldsHost.Children.Add(FieldBlock(Loc.T("Tool.VideoWall.SelectType"), typeDrop));
        }

        var spec = SourceFormSchema.For(_kind);
        if (spec is null) return;

        foreach (var field in spec.Fields)
        {
            if (!_values.ContainsKey(field.Key) && field.DefaultValue is not null)
                _values[field.Key] = field.DefaultValue;
        }

        foreach (var field in SourceFieldVisibility.VisibleFields(spec, _values))
        {
            if (!_values.ContainsKey(field.Key) && field.DefaultValue is not null)
                _values[field.Key] = field.DefaultValue;

            _fieldsHost.Children.Add(new Label
            {
                Text = Loc.T(field.LabelKey),
                FontSize = 12,
                TextColor = _muted,
                Margin = new Thickness(0, 4, 0, 0)
            });

            if (!string.IsNullOrEmpty(field.HintKey))
            {
                _fieldsHost.Children.Add(new Label
                {
                    Text = Loc.T(field.HintKey),
                    FontSize = 11,
                    TextColor = _muted,
                    LineBreakMode = LineBreakMode.WordWrap
                });
            }

            View editor = field.Kind switch
            {
                SourceFieldKind.Password => MakePassword(field),
                SourceFieldKind.Number => MakeText(field, Keyboard.Numeric),
                SourceFieldKind.Select or SourceFieldKind.Radio when field.AllowCustomValue
                    => MakeEditableSelect(field),
                SourceFieldKind.Select or SourceFieldKind.Radio => MakeSelect(field),
                SourceFieldKind.Checkbox => MakeCheck(field),
                SourceFieldKind.File => MakeFile(field),
                _ => MakeText(field, Keyboard.Default)
            };
            _fieldsHost.Children.Add(editor);
        }
    }

    private Entry MakeText(SourceFieldSpec field, Keyboard keyboard)
    {
        var entry = new Entry
        {
            FontSize = 13,
            TextColor = _text,
            BackgroundColor = _card,
            Keyboard = keyboard,
            Placeholder = field.Placeholder is not null ? Loc.T(field.Placeholder) : null,
            Text = _values.GetValueOrDefault(field.Key) ?? ""
        };
        entry.TextChanged += (_, _) => _values[field.Key] = entry.Text;
        if (string.Equals(field.Key, "name", StringComparison.OrdinalIgnoreCase))
            _nameEntry = entry;
        return entry;
    }

    private View MakePassword(SourceFieldSpec field)
    {
        var initial = _values.TryGetValue(field.Key, out var v) ? v ?? "" : "";
        var box = new SecurePasswordBox
        {
            Password = initial,
            Placeholder = field.Placeholder is null ? Loc.T(field.LabelKey) : Loc.T(field.Placeholder),
            ShowPasswordTip = Loc.T("Tool.VideoWall.ShowPassword"),
            HidePasswordTip = Loc.T("Tool.VideoWall.HidePassword"),
            HorizontalOptions = LayoutOptions.Fill
        };
        box.ApplyTheme(_text, _muted, _line, _card);
        box.PasswordChanged += (_, _) => _values[field.Key] = box.Password;
        return box;
    }

    private View MakeSelect(SourceFieldSpec field)
    {
        var drop = new VideoWallDropDown(_text, _muted, _line, _card);
        var opts = field.Options.Select(o => Loc.T(o.LabelKey)).ToArray();
        drop.SetOptions(opts);
        var current = _values.GetValueOrDefault(field.Key) ?? field.DefaultValue;
        var pick = field.Options.ToList().FindIndex(o =>
            string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase));
        drop.SelectedIndex = Math.Max(0, pick);
        drop.BindFlyout(_flyout);
        drop.SelectionChanged += _ =>
        {
            var i = drop.SelectedIndex;
            if (i >= 0 && i < field.Options.Count)
            {
                _values[field.Key] = field.Options[i].Value;
                Rebuild();
            }
        };
        if (drop.SelectedIndex >= 0 && drop.SelectedIndex < field.Options.Count)
            _values[field.Key] = field.Options[drop.SelectedIndex].Value;
        return drop;
    }

    /// <summary>可输入可下拉：键入即新建；▾ 选已有分组。</summary>
    private View MakeEditableSelect(SourceFieldSpec field)
    {
        var entry = new Entry
        {
            FontSize = 13,
            TextColor = _text,
            BackgroundColor = _card,
            Placeholder = field.Placeholder is not null ? Loc.T(field.Placeholder) : null,
            Text = _values.GetValueOrDefault(field.Key) ?? "",
            HorizontalOptions = LayoutOptions.Fill
        };
        entry.TextChanged += (_, _) =>
        {
            var t = (entry.Text ?? "").Trim();
            _values[field.Key] = string.IsNullOrEmpty(t) ? null : t;
        };

        var arrowBtn = new Border
        {
            Stroke = _line,
            StrokeThickness = 1,
            BackgroundColor = _card,
            Padding = new Thickness(10, 0),
            HeightRequest = 36,
            MinimumHeightRequest = 36,
            WidthRequest = 40,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new Label
            {
                Text = "▾",
                FontSize = 11,
                TextColor = _muted,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            }
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => ShowEditableOptions(field, entry, arrowBtn);
        arrowBtn.GestureRecognizers.Add(tap);

        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            ColumnSpacing = 6,
            Padding = new Thickness(0, 0, 4, 0),
            Children = { entry, arrowBtn }
        };
        Grid.SetColumn(entry, 0);
        Grid.SetColumn(arrowBtn, 1);
        return grid;
    }

    private void ShowEditableOptions(SourceFieldSpec field, Entry entry, View anchor)
    {
        var options = string.Equals(field.Key, "groupId", StringComparison.OrdinalIgnoreCase)
            ? _groupNames().ToArray()
            : field.Options.Select(o => Loc.T(o.LabelKey)).ToArray();
        if (options.Length == 0) return;

        _flyout.ShowAt(anchor, BuildOptionsMenu(options, v =>
        {
            entry.Text = v;
            _values[field.Key] = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            _flyout.Close();
        }), Math.Max(entry.Width > 0 ? entry.Width + 46 : 200, 160));
    }

    private Border BuildOptionsMenu(string[] options, Action<string> onPick)
    {
        var items = new VerticalStackLayout { Spacing = 0 };
        var hover = Color.FromArgb("#EFF6FF");
        foreach (var opt in options)
        {
            var label = new Label
            {
                Text = opt,
                FontSize = 13,
                TextColor = _text,
                Padding = new Thickness(12, 10)
            };
            var item = new Border
            {
                StrokeThickness = 0,
                BackgroundColor = Colors.Transparent,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                Content = label
            };
            var local = opt;
            var itemTap = new TapGestureRecognizer();
            itemTap.Tapped += (_, _) => onPick(local);
            item.GestureRecognizers.Add(itemTap);
            var ptr = new PointerGestureRecognizer();
            ptr.PointerEntered += (_, _) => item.BackgroundColor = hover;
            ptr.PointerExited += (_, _) => item.BackgroundColor = Colors.Transparent;
            item.GestureRecognizers.Add(ptr);
            items.Children.Add(item);
        }

        return new Border
        {
            Stroke = _line,
            StrokeThickness = 1,
            BackgroundColor = _card,
            Padding = new Thickness(4),
            MinimumWidthRequest = 160,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Offset = new Point(0, 4),
                Radius = 12,
                Opacity = 0.18f
            },
            Content = items,
            ZIndex = 210
        };
    }

    private View MakeCheck(SourceFieldSpec field)
    {
        var on = string.Equals(
            _values.GetValueOrDefault(field.Key) ?? field.DefaultValue,
            "true",
            StringComparison.OrdinalIgnoreCase);
        var cb = new CheckBox { IsChecked = on };
        _values[field.Key] = on ? "true" : "false";
        cb.CheckedChanged += (_, e) => _values[field.Key] = e.Value ? "true" : "false";
        return cb;
    }

    private View MakeFile(SourceFieldSpec field)
    {
        var entry = new Entry
        {
            IsReadOnly = true,
            FontSize = 13,
            TextColor = _text,
            BackgroundColor = _card,
            Text = _values.GetValueOrDefault(field.Key) ?? "",
            HorizontalOptions = LayoutOptions.Fill
        };
        _pathEntries[field.Key] = entry;
        entry.TextChanged += (_, _) => _values[field.Key] = entry.Text;

        var pickBtn = new Button
        {
            Text = "…",
            FontSize = 16,
            Padding = new Thickness(12, 6),
            Margin = new Thickness(0, 0, 2, 0),
            MinimumWidthRequest = 40,
            BackgroundColor = _card,
            TextColor = _text,
            BorderColor = _line,
            BorderWidth = 1,
            CornerRadius = 6,
            // 抬高命中，避免被 ScrollView 滚动条抢点击
            ZIndex = 2
        };
        pickBtn.Clicked += async (_, _) => await PickFileAsync(field.Key, entry);

        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            ColumnSpacing = 8,
            Padding = new Thickness(0, 0, 4, 0),
            Children = { entry, pickBtn }
        };
        Grid.SetColumn(entry, 0);
        Grid.SetColumn(pickBtn, 1);
        return grid;
    }

    private async Task PickFileAsync(string key, Entry entry)
    {
        try
        {
            string? path = null;
#if WINDOWS
            // unpackaged / 管理员下 MAUI FilePicker 常失败，改走 Win32
            path = await MainThread.InvokeOnMainThreadAsync(() =>
                VideoWallWin32PathPicker.PickSingleFile(
                    title: Loc.T("Tool.VideoWall.Field.FilePath")));
#else
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = Loc.T("Tool.VideoWall.Field.FilePath"),
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "video/*", "*/*" } },
                    { DevicePlatform.iOS, new[] { "public.movie", "public.data" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.movie", "public.data" } }
                })
            });
            if (result is null) return;
            path = result.FullPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                await using var stream = await result.OpenReadAsync();
                path = Path.Combine(Path.GetTempPath(), result.FileName);
                await using var fs = File.Create(path);
                await stream.CopyToAsync(fs);
            }
#endif
            if (string.IsNullOrWhiteSpace(path)) return;
            entry.Text = path;
            _values[key] = path;
            SuggestNameFromPath(path);
        }
        catch
        {
            // 平台不支持或取消时忽略
        }
    }

    private void SuggestNameFromPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(_values.GetValueOrDefault("name"))) return;
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) return;
        _values["name"] = name;
        if (_nameEntry is not null)
            _nameEntry.Text = name;
    }

    private View FieldBlock(string label, View control)
        => new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = label, FontSize = 12, TextColor = _muted },
                control
            }
        };

    private static string KindLabel(VideoSourceKind kind) => kind switch
    {
        VideoSourceKind.Camera => Loc.T("Tool.VideoWall.Type.Camera"),
        VideoSourceKind.NetworkUrl => Loc.T("Tool.VideoWall.Type.Network"),
        VideoSourceKind.LocalFile => Loc.T("Tool.VideoWall.Type.LocalFile"),
        VideoSourceKind.NvrChannel => Loc.T("Tool.VideoWall.Type.Nvr"),
        _ => kind.ToString()
    };
}
