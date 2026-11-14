using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    private void RebuildFields()
    {
        FieldsPanel.Children.Clear();
        var spec = SourceFormSchema.For(_kind);
        if (spec is null) return;

        foreach (var field in SourceFieldVisibility.VisibleFields(spec, _values))
        {
            if (!_values.ContainsKey(field.Key) && field.DefaultValue is not null)
                _values[field.Key] = field.DefaultValue;

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

    private PasswordBox CreatePassword(SourceFieldSpec field)
    {
        var pb = new PasswordBox { Tag = field.Key };
        pb.PasswordChanged += (_, _) => _values[field.Key] = pb.Password;
        return pb;
    }

    private TextBox CreateNumber(SourceFieldSpec field)
    {
        var tb = CreateText(field);
        return tb;
    }

    private UIElement CreateSelect(SourceFieldSpec field)
    {
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
            if (dlg.ShowDialog() == true)
            {
                tb.Text = dlg.FileName;
                _values[field.Key] = dlg.FileName;
            }
        };
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);
        return grid;
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        SaveBtn.IsEnabled = false;
        try
        {
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