using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;

namespace FlyScreen.Tool.VideoWall.Ui.Desktop;

public partial class AddSourceWindow : Window
{
    private readonly IVideoWallService? _service;
    private readonly string? _editingId;
    private readonly VideoSourceKind? _fixedKind;

    /// <summary>供 Avalonia 资源加载器发现；运行时请用带服务参数的构造。</summary>
    public AddSourceWindow()
    {
        InitializeComponent();
    }

    public AddSourceWindow(IVideoWallService service, VideoSourceDescriptor? edit = null) : this()
    {
        _service = service;
        _editingId = edit?.Id;
        _fixedKind = edit?.Kind;
        ApplyLoc();
        TypeCombo.ItemsSource = SourceFormSchema.Types
            .Where(t => t.Implemented && t.Kind != VideoSourceKind.NvrChannel)
            .Select(t => new ComboBoxItem { Content = Loc.T(t.LabelKey), Tag = t.Kind })
            .Cast<object>()
            .ToList();

        if (edit is not null)
        {
            var kinds = SourceFormSchema.Types
                .Where(t => t.Implemented && t.Kind != VideoSourceKind.NvrChannel)
                .Select(t => t.Kind)
                .ToList();
            TypeCombo.SelectedIndex = Math.Max(0, kinds.IndexOf(edit.Kind));
            TypeCombo.IsEnabled = false;
            NameBox.Text = edit.Name;
            AddressBox.Text = edit.Endpoint.Address;
        }
        else
        {
            TypeCombo.SelectedIndex = 0;
        }

        AuthCombo.ItemsSource = new List<string>
        {
            Loc.T("Tool.VideoWall.Auth.None"),
            Loc.T("Tool.VideoWall.Auth.Basic"),
            Loc.T("Tool.VideoWall.Auth.Digest")
        };
        AuthCombo.SelectedIndex = 0;
    }

    public bool Saved { get; private set; }

    private void ApplyLoc()
    {
        Title = _editingId is null ? Loc.T("Tool.VideoWall.AddSource") : Loc.T("Tool.VideoWall.Editing");
        TitleText.Text = Title;
        TypeLabel.Text = Loc.T("Tool.VideoWall.SelectType");
        NameLabel.Text = Loc.T("Tool.VideoWall.Field.Name");
        AddressLabel.Text = Loc.T("Tool.VideoWall.Field.Address");
        AuthLabel.Text = Loc.T("Tool.VideoWall.Field.Auth");
        UserLabel.Text = Loc.T("Tool.VideoWall.Field.Username");
        PassLabel.Text = Loc.T("Tool.VideoWall.Field.Password");
        NameBox.Watermark = Loc.T("Tool.VideoWall.Field.Name");
        AddressBox.Watermark = Loc.T("Tool.VideoWall.Field.Address");
        UserBox.Watermark = Loc.T("Tool.VideoWall.Field.Username");
        PassBox.Watermark = Loc.T("Tool.VideoWall.Field.Password");
        CancelBtn.Content = Loc.T("Tool.VideoWall.Cancel");
        SaveBtn.Content = Loc.T("Tool.VideoWall.Save");
    }

    private VideoSourceKind SelectedKind()
    {
        if (_fixedKind is { } fixedKind)
            return fixedKind;
        if (TypeCombo.SelectedItem is ComboBoxItem item && item.Tag is VideoSourceKind k)
            return k;
        return VideoSourceKind.Camera;
    }

    private static string AuthValue(int index) => index switch
    {
        1 => SourceFormSchema.AuthBasic,
        2 => SourceFormSchema.AuthDigest,
        _ => SourceFormSchema.AuthNone
    };

    private async void SaveBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;

        ErrorText.Text = "";
        var kind = SelectedKind();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = NameBox.Text,
            ["address"] = AddressBox.Text,
            ["path"] = kind == VideoSourceKind.LocalFile ? AddressBox.Text : null,
            ["auth"] = AuthValue(AuthCombo.SelectedIndex),
            ["username"] = UserBox.Text,
            ["password"] = PassBox.Text,
            ["decode"] = "auto",
            ["bufferMs"] = "800",
            ["transport"] = "tcp"
        };

        var descriptor = SourceDescriptorBuilder.TryBuild(kind, values, _service.Credentials, out var err);
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

        await _service.RefreshCoverAsync(descriptor);
        Saved = true;
        Close();
    }

    private void CancelBtn_Click(object? sender, RoutedEventArgs e) => Close();
}
