using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    private string? _editingCredentialRef;

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

        GroupCombo.ItemsSource = service.Catalog.Groups.Select(g => g.Name).ToList();
        GroupCombo.PlaceholderText = Loc.T("Tool.VideoWall.Field.GroupPlaceholder");

        AuthCombo.ItemsSource = new List<string>
        {
            Loc.T("Tool.VideoWall.Auth.None"),
            Loc.T("Tool.VideoWall.Auth.Basic"),
            Loc.T("Tool.VideoWall.Auth.Digest")
        };
        AuthCombo.SelectedIndex = 0;

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
            GroupCombo.Text = edit.GroupId ?? "";
            HeadersBox.Text = SourceHeaderText.Format(edit.Endpoint.Headers);
            if (!string.IsNullOrWhiteSpace(edit.Endpoint.CredentialRef))
            {
                var cred = service.Credentials.Get(edit.Endpoint.CredentialRef);
                if (cred is not null)
                {
                    _editingCredentialRef = edit.Endpoint.CredentialRef;
                    AuthCombo.SelectedIndex = cred.AuthKind switch
                    {
                        SourceFormSchema.AuthDigest => 2,
                        SourceFormSchema.AuthBasic => 1,
                        _ => 1
                    };
                    UserBox.Text = cred.Username;
                    PassBox.Password = cred.Password;
                }
            }
        }
        else
        {
            TypeCombo.SelectedIndex = 0;
        }

        TypeCombo.SelectionChanged += (_, _) => UpdateLocalFileUi();
        UpdateLocalFileUi();
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
        GroupLabel.Text = Loc.T("Tool.VideoWall.Field.Group");
        NameBox.Watermark = Loc.T("Tool.VideoWall.Field.Name");
        AddressBox.Watermark = Loc.T("Tool.VideoWall.Field.Address");
        UserBox.Watermark = Loc.T("Tool.VideoWall.Field.Username");
        PassBox.Placeholder = Loc.T("Tool.VideoWall.Field.Password");
        PassBox.ShowPasswordTip = Loc.T("Tool.VideoWall.ShowPassword");
        PassBox.HidePasswordTip = Loc.T("Tool.VideoWall.HidePassword");
        HeadersLabel.Text = Loc.T("Tool.VideoWall.Field.Headers");
        HeadersHint.Text = Loc.T("Tool.VideoWall.Hint.Headers");
        BrowseBtn.Content = "…";
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

    private void UpdateLocalFileUi()
    {
        var local = SelectedKind() == VideoSourceKind.LocalFile;
        FileBrowseRow.IsVisible = local;
        HeadersRow.IsVisible = !local;
        AddressLabel.Text = local
            ? Loc.T("Tool.VideoWall.Field.FilePath")
            : Loc.T("Tool.VideoWall.Field.Address");
    }

    private static string AuthValue(int index) => index switch
    {
        1 => SourceFormSchema.AuthBasic,
        2 => SourceFormSchema.AuthDigest,
        _ => SourceFormSchema.AuthNone
    };

    private async void BrowseBtn_Click(object? sender, RoutedEventArgs e)
    {
        var top = StorageProvider;
        if (top is null) return;
        var files = await top.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Tool.VideoWall.Field.FilePath"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.ts", "*.m4v", "*.wmv", "*.flv", "*.webm"]
                },
                new FilePickerFileType("All") { Patterns = ["*.*"] }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        AddressBox.Text = path;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileNameWithoutExtension(path);
    }

    private async void SaveBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;

        ErrorText.Text = "";
        var kind = SelectedKind();
        var group = (GroupCombo.Text ?? "").Trim();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = NameBox.Text,
            ["address"] = AddressBox.Text,
            ["path"] = kind == VideoSourceKind.LocalFile ? AddressBox.Text : null,
            ["auth"] = AuthValue(AuthCombo.SelectedIndex),
            ["username"] = UserBox.Text,
            ["password"] = PassBox.Password,
            ["credentialRef"] = _editingCredentialRef,
            ["headers"] = kind == VideoSourceKind.LocalFile ? null : HeadersBox.Text,
            ["decode"] = "auto",
            ["bufferMs"] = "800",
            ["transport"] = "tcp",
            ["groupId"] = string.IsNullOrEmpty(group) ? null : group
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
