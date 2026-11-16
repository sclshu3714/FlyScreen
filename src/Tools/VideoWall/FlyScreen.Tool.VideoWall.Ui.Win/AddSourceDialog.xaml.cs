using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;
using Microsoft.Win32;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

public partial class AddSourceDialog : Window
{
    private readonly IVideoWallService _service;
    private readonly string? _editingId;
    private VideoSourceKind _kind = VideoSourceKind.Camera;
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public AddSourceDialog(IVideoWallService service, Window? owner)
        : this(service, owner, null)
    {
    }

    public AddSourceDialog(IVideoWallService service, Window? owner, VideoSourceDescriptor? edit)
    {
        InitializeComponent();
        _service = service;
        _editingId = edit?.Id;
        if (edit is not null)
        {
            _kind = edit.Kind;
            _values["name"] = edit.Name;
            _values["address"] = edit.Endpoint.Address;
            if (edit.Kind == VideoSourceKind.LocalFile)
                _values["path"] = edit.Endpoint.Address;
            _values["groupId"] = edit.GroupId;
            _values["decode"] = edit.DecodePreference;
            _values["note"] = edit.Note;
            if (edit.Endpoint.BufferMs is int buf)
                _values["bufferMs"] = buf.ToString();
            if (!string.IsNullOrWhiteSpace(edit.Endpoint.Transport))
                _values["transport"] = edit.Endpoint.Transport;
            LoadCredentialFields(edit);
        }

        if (owner is not null)
        {
            Owner = owner;
            InheritHostBrushes(owner);
        }

        Loaded += (_, _) => BuildTypeRadios();
        ApplyLoc();
    }

    /// <summary>从宿主窗口拷贝主题色，使插件对话框与主程序一致。</summary>
    private void InheritHostBrushes(FrameworkElement host)
    {
        foreach (var key in new[]
                 {
                     "Blue", "BluePress", "BlueSoft", "BlueHover", "Muted", "Line", "Text",
                     "WinBg", "CardBg", "ControlBg", "ControlBorder"
                 })
        {
            if (host.TryFindResource(key) is SolidColorBrush brush)
                Resources[key] = brush.Clone();
        }
    }

    public bool Saved { get; private set; }

    private void ApplyLoc()
    {
        Title = _editingId is null ? Loc.T("Tool.VideoWall.AddSource") : Loc.T("Tool.VideoWall.Editing");
        TitleText.Text = _editingId is null ? Loc.T("Tool.VideoWall.SelectType") : Loc.T("Tool.VideoWall.Editing");
        TypeLabel.Text = Loc.T("Tool.VideoWall.SelectType");
        CancelBtn.Content = Loc.T("Tool.VideoWall.Cancel");
        SaveBtn.Content = Loc.T("Tool.VideoWall.Save");
    }

    private void BuildTypeRadios()
    {
        TypePanel.Children.Clear();
        foreach (var spec in SourceFormSchema.Types.Where(t => t.Implemented && t.Kind != VideoSourceKind.NvrChannel))
        {
            var rb = new RadioButton
            {
                Content = Loc.T(spec.LabelKey),
                Tag = spec.Kind,
                GroupName = "SourceType",
                IsChecked = spec.Kind == _kind,
                IsEnabled = _editingId is null
            };
            rb.Checked += (_, _) =>
            {
                _kind = (VideoSourceKind)rb.Tag!;
                RebuildFields();
            };
            TypePanel.Children.Add(rb);
        }

        RebuildFields();
    }

    private void LoadCredentialFields(VideoSourceDescriptor edit)
    {
        var credRef = edit.Endpoint.CredentialRef;
        if (string.IsNullOrWhiteSpace(credRef)) return;
        var cred = _service.Credentials.Get(credRef);
        if (cred is null) return;

        _values["credentialRef"] = credRef;
        _values["auth"] = string.IsNullOrWhiteSpace(cred.AuthKind)
            ? SourceFormSchema.AuthBasic
            : cred.AuthKind;
        _values["username"] = cred.Username;
        _values["password"] = cred.Password;
    }

    private void RebuildFields()
    {
        FieldsPanel.Children.Clear();
        var spec = SourceFormSchema.For(_kind);
        if (spec is null) return;

        // 先写入缺省值，再算显隐（否则 auth 未写入时 username/password 会误显示）
        foreach (var field in spec.Fields)
        {
            if (!_values.ContainsKey(field.Key) && field.DefaultValue is not null)
                _values[field.Key] = field.DefaultValue;
        }

        foreach (var field in SourceFieldVisibility.VisibleFields(spec, _values))
        {
            var label = new TextBlock
            {
                Text = Loc.T(field.LabelKey),
                Margin = new Thickness(0, 8, 0, 4),
                FontSize = 12
            };
            FieldsPanel.Children.Add(label);

            if (!string.IsNullOrEmpty(field.HintKey))
            {
                var hint = new TextBlock
                {
                    Text = Loc.T(field.HintKey),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                FieldsPanel.Children.Add(hint);
            }

            UIElement editor = field.Kind switch
            {
                SourceFieldKind.Password => CreatePassword(field),
                SourceFieldKind.Number => CreateNumber(field),
                SourceFieldKind.Select or SourceFieldKind.Radio => CreateSelect(field),
                SourceFieldKind.Checkbox => CreateCheck(field),
                SourceFieldKind.File => CreateFile(field),
                _ => CreateText(field)
            };
            FieldsPanel.Children.Add(editor);
        }
    }

    private TextBox CreateText(SourceFieldSpec field)
    {
        var tb = new TextBox { Tag = field.Key };
        if (field.Placeholder is not null)
            tb.ToolTip = Loc.T(field.Placeholder);
        if (_values.TryGetValue(field.Key, out var v))
            tb.Text = v ?? "";
        tb.TextChanged += (_, _) => _values[field.Key] = tb.Text;
        return tb;
    }

    private static readonly Geometry EyeOpenGeometry =
        Geometry.Parse(
            "M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9Z");

    private static readonly Geometry EyeOffGeometry =
        Geometry.Parse(
            "M11.83,9L15,12.16C15,12.11 15,12.05 15,12A3,3 0 0,0 12,9C11.94,9 11.89,9 11.83,9M7.53,9.8L9.08,11.35C9.03,11.56 9,11.77 9,12A3,3 0 0,0 12,15C12.22,15 12.44,14.97 12.65,14.92L14.2,16.47C13.53,16.8 12.79,17 12,17A5,5 0 0,1 7,12C7,11.21 7.2,10.47 7.53,9.8M2,4.27L4.28,6.55L4.73,7C3.08,8.3 1.78,10 1,12C2.73,16.39 7,19.5 12,19.5C13.55,19.5 15.03,19.2 16.38,18.66L16.81,19.08L19.73,22L21,20.73L3.27,3M12,7A5,5 0 0,1 17,12C17,12.64 16.87,13.26 16.64,13.82L19.57,16.75C21.07,15.5 22.27,13.86 23,12C21.27,7.61 17,4.5 12,4.5C10.6,4.5 9.26,4.75 8,5.2L10.17,7.35C10.74,7.13 11.35,7 12,7Z");

    /// <summary>密码框：默认掩码回显；右侧嵌入眼睛图标在 PasswordBox / TextBox 间切换。</summary>
    private UIElement CreatePassword(SourceFieldSpec field)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputHost = new Grid { Tag = field.Key };
        var initial = _values.TryGetValue(field.Key, out var v) ? v ?? "" : "";

        var pb = new PasswordBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            Password = initial
        };
        var tb = new TextBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = initial,
            Visibility = Visibility.Collapsed
        };

        var syncing = false;
        pb.PasswordChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            tb.Text = pb.Password;
            _values[field.Key] = pb.Password;
            syncing = false;
        };
        tb.TextChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            pb.Password = tb.Text ?? "";
            _values[field.Key] = tb.Text;
            syncing = false;
        };

        inputHost.Children.Add(pb);
        inputHost.Children.Add(tb);
        Grid.SetColumn(inputHost, 0);
        grid.Children.Add(inputHost);

        var eyePath = new Path
        {
            Data = EyeOpenGeometry,
            Stretch = Stretch.Uniform,
            Width = 16,
            Height = 16,
            Fill = TryFindResource("Muted") as Brush ?? Brushes.Gray
        };
        var eyeBtn = new Button
        {
            Content = new Viewbox { Width = 16, Height = 16, Child = eyePath },
            Width = 36,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            ToolTip = Loc.T("Tool.VideoWall.ShowPassword"),
            Focusable = false,
            Style = TryFindResource("DlgBtn") as Style
        };
        var hidden = true;
        eyeBtn.Click += (_, _) =>
        {
            hidden = !hidden;
            if (hidden)
            {
                syncing = true;
                pb.Password = tb.Text ?? "";
                syncing = false;
                tb.Visibility = Visibility.Collapsed;
                pb.Visibility = Visibility.Visible;
                eyePath.Data = EyeOpenGeometry;
                eyeBtn.ToolTip = Loc.T("Tool.VideoWall.ShowPassword");
            }
            else
            {
                syncing = true;
                tb.Text = pb.Password;
                syncing = false;
                pb.Visibility = Visibility.Collapsed;
                tb.Visibility = Visibility.Visible;
                eyePath.Data = EyeOffGeometry;
                eyeBtn.ToolTip = Loc.T("Tool.VideoWall.HidePassword");
                tb.Focus();
                tb.CaretIndex = tb.Text?.Length ?? 0;
            }
        };
        Grid.SetColumn(eyeBtn, 1);
        grid.Children.Add(eyeBtn);
        return grid;
    }

    private TextBox CreateNumber(SourceFieldSpec field)
    {
        var tb = CreateText(field);
        return tb;
    }

    private UIElement CreateSelect(SourceFieldSpec field)
    {
        if (field.AllowCustomValue)
            return CreateEditableSelect(field);

        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 0), Tag = field.Key };
        foreach (var opt in field.Options)
            combo.Items.Add(new ComboBoxItem { Content = Loc.T(opt.LabelKey), Tag = opt.Value });
        var current = _values.GetValueOrDefault(field.Key) ?? field.DefaultValue;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                break;
            }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item)
            {
                _values[field.Key] = item.Tag as string;
                RebuildFields();
            }
        };
        return combo;
    }

    /// <summary>可输入可下拉：选已有分组，或键入名称新建。</summary>
    private UIElement CreateEditableSelect(SourceFieldSpec field)
    {
        var combo = new ComboBox
        {
            Tag = field.Key,
            IsEditable = true,
            IsTextSearchEnabled = true,
            StaysOpenOnEdit = true
        };
        if (field.Placeholder is not null)
            combo.ToolTip = Loc.T(field.Placeholder);

        // 分组：候选项来自目录（非 schema Options）
        if (string.Equals(field.Key, "groupId", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var g in _service.Catalog.Groups)
                combo.Items.Add(g.Name);
        }
        else
        {
            foreach (var opt in field.Options)
                combo.Items.Add(new ComboBoxItem { Content = Loc.T(opt.LabelKey), Tag = opt.Value });
        }

        var current = _values.GetValueOrDefault(field.Key) ?? field.DefaultValue ?? "";
        combo.Text = current;
        if (!string.IsNullOrWhiteSpace(current))
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                var text = combo.Items[i] switch
                {
                    string s => s,
                    ComboBoxItem { Content: string c } => c,
                    ComboBoxItem item => item.Content?.ToString(),
                    _ => combo.Items[i]?.ToString()
                };
                if (string.Equals(text, current, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    break;
                }
            }
        }

        void Sync()
        {
            var text = (combo.Text ?? "").Trim();
            _values[field.Key] = string.IsNullOrEmpty(text) ? null : text;
        }

        combo.SelectionChanged += (_, _) => Sync();
        combo.LostFocus += (_, _) => Sync();
        combo.DropDownClosed += (_, _) => Sync();
        combo.Loaded += (_, _) =>
        {
            if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox tb)
                tb.TextChanged += (_, _) => Sync();
        };
        return combo;
    }

    private CheckBox CreateCheck(SourceFieldSpec field)
    {
        var cb = new CheckBox { Tag = field.Key };
        var on = string.Equals(_values.GetValueOrDefault(field.Key) ?? field.DefaultValue, "true", StringComparison.OrdinalIgnoreCase);
        cb.IsChecked = on;
        _values[field.Key] = on ? "true" : "false";
        cb.Checked += (_, _) => _values[field.Key] = "true";
        cb.Unchecked += (_, _) => _values[field.Key] = "false";
        return cb;
    }

    private Grid CreateFile(SourceFieldSpec field)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = new TextBox
        {
            Tag = field.Key,
            IsReadOnly = true
        };
        if (_values.TryGetValue(field.Key, out var v))
            tb.Text = v ?? "";
        tb.TextChanged += (_, _) => _values[field.Key] = tb.Text;
        Grid.SetColumn(tb, 0);
        grid.Children.Add(tb);

        var btn = new Button
        {
            Content = "…",
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 36,
            Style = TryFindResource("DlgBtn") as Style
        };
        btn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Video|*.mp4;*.mkv;*.avi;*.mov;*.ts|All|*.*" };
            if (dlg.ShowDialog() != true) return;
            tb.Text = dlg.FileName;
            _values[field.Key] = dlg.FileName;
            SuggestNameFromPath(dlg.FileName);
        };
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);
        return grid;
    }

    private void FlushEditableFields()
    {
        foreach (var child in FieldsPanel.Children)
        {
            if (child is ComboBox { IsEditable: true, Tag: string key } combo)
            {
                var text = (combo.Text ?? "").Trim();
                _values[key] = string.IsNullOrEmpty(text) ? null : text;
            }
        }
    }

    /// <summary>选本地文件后，若名称为空则用文件名（不含扩展名）。</summary>
    private void SuggestNameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!string.IsNullOrWhiteSpace(_values.GetValueOrDefault("name"))) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) return;
        _values["name"] = name;
        foreach (var child in FieldsPanel.Children)
        {
            if (child is TextBox { Tag: "name" } tb)
            {
                tb.Text = name;
                break;
            }
        }
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        SaveBtn.IsEnabled = false;
        try
        {
            FlushEditableFields();
            var descriptor = SourceDescriptorBuilder.TryBuild(_kind, _values, _service.Credentials, out var err);
            if (descriptor is null)
            {
                ErrorText.Text = err ?? "无法保存";
                return;
            }

            if (_editingId is not null)
                descriptor = descriptor with { Id = _editingId };

            _service.Credentials.Save();

            var add = _editingId is null
                ? await _service.AddSourceAsync(descriptor)
                : await _service.UpdateSourceAsync(descriptor);
            if (!add.Added)
            {
                ErrorText.Text = add.Reason;
                return;
            }

            SaveBtn.Content = Loc.T("Tool.VideoWall.Capturing");
            var cover = await _service.RefreshCoverAsync(descriptor);
            if (!cover.Ok)
                ErrorText.Text = Loc.T("Tool.VideoWall.CaptureFailed");

            Saved = true;
            DialogResult = true;
            Close();
        }
        finally
        {
            SaveBtn.IsEnabled = true;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}