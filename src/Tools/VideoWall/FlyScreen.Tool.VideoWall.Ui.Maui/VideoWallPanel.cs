using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>Maui 视频墙工作区：表格 + 工具栏添加/批量删除 + 居中添加/编辑弹窗。</summary>
public sealed class VideoWallPanel : ContentView
{
    private readonly IVideoWallService? _service;
    private readonly ObservableCollection<SourceRow> _rows = new();
    private readonly CollectionView _table;
    private readonly Label _status;
    private readonly Label _empty;
    private readonly Entry _search;
    private readonly CheckBox _selectAll;
    private readonly Button _deleteBtn;
    private readonly AbsoluteLayout _root;
    private readonly Border _dialogCard;
    private readonly Label _dialogTitle;
    private readonly VideoWallDropDown _kindDrop;
    private readonly Entry _name;
    private readonly Entry _address;
    private readonly VideoWallDropDown _authDrop;
    private readonly Entry _user;
    private readonly Entry _pass;
    private readonly Label _formError;
    private readonly VideoWallFlyoutHost _flyout;
    private readonly Color _text;
    private readonly Color _muted;
    private readonly Color _line;
    private readonly Color _card;
    private readonly Color _blue;
    private string? _playingId;
    private string? _editingId;
    private bool _suppressSelectAll;

    public Action<string>? StatusReporter { get; set; }

    public VideoWallPanel()
    {
        _service = ToolService.Instance.GetToolService<IVideoWallService>();

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        _text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        _muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        _line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        _card = Color.FromArgb(dark ? "#1E293B" : "#FFFFFF");
        _blue = Color.FromArgb("#2563EB");
        var warn = Color.FromArgb("#DC2626");

        _status = new Label { FontSize = 12, TextColor = _muted, Margin = new Thickness(0, 8, 0, 0) };
        _empty = new Label
        {
            Text = Loc.T("Tool.VideoWall.EmptyTitle") + "\n" + Loc.T("Tool.VideoWall.EmptyHint"),
            FontSize = 13,
            TextColor = _muted,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false
        };

        _search = new Entry
        {
            Placeholder = Loc.T("Tool.VideoWall.Search"),
            FontSize = 13,
            HorizontalOptions = LayoutOptions.Fill,
            BackgroundColor = _card
        };
        _search.TextChanged += (_, _) => RefreshList();

        var addBtn = MakeBtn(Loc.T("Tool.VideoWall.Add"), _blue, Colors.White);
        addBtn.Clicked += (_, _) => OpenDialog(edit: null);
        _deleteBtn = MakeBtn(Loc.T("Tool.VideoWall.Delete"), _card, warn, _line);
        _deleteBtn.Clicked += async (_, _) => await DeleteSelectedAsync();
        _deleteBtn.IsEnabled = false;

        _selectAll = new CheckBox { VerticalOptions = LayoutOptions.Center };
        _selectAll.CheckedChanged += (_, e) =>
        {
            if (_suppressSelectAll) return;
            foreach (var row in _rows)
                row.IsSelected = e.Value;
            UpdateDeleteEnabled();
        };

        var toolbar = new Border
        {
            Stroke = _line,
            BackgroundColor = _card,
            Padding = new Thickness(10, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)
                ],
                ColumnSpacing = 8,
                Children =
                {
                    new HorizontalStackLayout
                    {
                        Spacing = 4,
                        VerticalOptions = LayoutOptions.Center,
                        Children =
                        {
                            _selectAll,
                            new Label
                            {
                                Text = Loc.T("Tool.VideoWall.SelectAll"),
                                TextColor = _text,
                                FontSize = 13,
                                VerticalOptions = LayoutOptions.Center
                            }
                        }
                    },
                    _search.Also(v => Grid.SetColumn(v, 2)),
                    addBtn.Also(v => Grid.SetColumn(v, 3)),
                    _deleteBtn.Also(v => Grid.SetColumn(v, 4))
                }
            }
        };

        var header = BuildHeaderRow();
        _table = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() => BuildDataRow())
        };

        var tableHost = new Border
        {
            Stroke = _line,
            BackgroundColor = _card,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(0, 0, 0, 4),
            Content = new Grid
            {
                RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)],
                Children =
                {
                    header,
                    new Grid
                    {
                        Children = { _table, _empty }
                    }.Also(v => Grid.SetRow(v, 1))
                }
            }
        };

        var main = new Grid
        {
            Padding = 8,
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            ],
            Children =
            {
                toolbar,
                tableHost.Also(v => Grid.SetRow(v, 1)),
                _status.Also(v => Grid.SetRow(v, 2))
            }
        };

        var flyoutLayer = new AbsoluteLayout
        {
            IsVisible = false,
            BackgroundColor = Colors.Transparent,
            ZIndex = 300
        };
        _flyout = new VideoWallFlyoutHost(flyoutLayer);

        var kindLabels = SourceFormSchema.Types
            .Where(x => x.Implemented && x.Kind != VideoSourceKind.NvrChannel)
            .Select(t => Loc.T(t.LabelKey))
            .ToArray();
        _kindDrop = new VideoWallDropDown(_text, _muted, _line, _card);
        _kindDrop.SetOptions(kindLabels);
        _kindDrop.SelectedIndex = 0;
        _kindDrop.BindFlyout(_flyout);

        var authLabels = new[]
        {
            Loc.T("Tool.VideoWall.Auth.None"),
            Loc.T("Tool.VideoWall.Auth.Basic"),
            Loc.T("Tool.VideoWall.Auth.Digest")
        };
        _authDrop = new VideoWallDropDown(_text, _muted, _line, _card);
        _authDrop.SetOptions(authLabels);
        _authDrop.SelectedIndex = 0;
        _authDrop.BindFlyout(_flyout);

        _name = MakeEntry(Loc.T("Tool.VideoWall.Field.Name"));
        _address = MakeEntry(Loc.T("Tool.VideoWall.Field.Address"));
        _user = MakeEntry(Loc.T("Tool.VideoWall.Field.Username"));
        _pass = MakeEntry(Loc.T("Tool.VideoWall.Field.Password"));
        _pass.IsPassword = true;
        _formError = new Label { TextColor = warn, FontSize = 12, IsVisible = false };
        _dialogTitle = new Label
        {
            FontAttributes = FontAttributes.Bold,
            FontSize = 16,
            TextColor = _text
        };

        var saveBtn = MakeBtn(Loc.T("Tool.VideoWall.Save"), _blue, Colors.White);
        saveBtn.Clicked += async (_, _) => await SaveDialogAsync();
        var cancelBtn = MakeBtn(Loc.T("Tool.VideoWall.Cancel"), _card, _text, _line);
        cancelBtn.Clicked += (_, _) => CloseDialog();

        var formBody = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                FieldBlock(Loc.T("Tool.VideoWall.SelectType"), _kindDrop),
                FieldBlock(Loc.T("Tool.VideoWall.Field.Name"), _name),
                FieldBlock(Loc.T("Tool.VideoWall.Field.Address"), _address),
                FieldBlock(Loc.T("Tool.VideoWall.Field.Auth"), _authDrop),
                FieldBlock(Loc.T("Tool.VideoWall.Field.Username"), _user),
                FieldBlock(Loc.T("Tool.VideoWall.Field.Password"), _pass)
            }
        };

        var formScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Vertical,
            VerticalScrollBarVisibility = ScrollBarVisibility.Always,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = formBody
        };

        var footer = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                _formError,
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    HorizontalOptions = LayoutOptions.End,
                    Children = { cancelBtn, saveBtn }
                }
            }
        };

        // 标题固定 + 表单可滚动 + 底部按钮固定，避免矮视口裁切
        var dialogInner = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            ],
            RowSpacing = 10,
            Children =
            {
                _dialogTitle,
                formScroll.Also(v => Grid.SetRow(v, 1)),
                footer.Also(v => Grid.SetRow(v, 2))
            }
        };

        _dialogCard = new Border
        {
            Stroke = _line,
            BackgroundColor = _card,
            Padding = 16,
            WidthRequest = 480,
            MaximumWidthRequest = 560,
            MaximumHeightRequest = 520,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(24),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Offset = new Point(0, 8),
                Radius = 24,
                Opacity = 0.22f
            },
            Content = dialogInner
        };

        var dim = new BoxView { Color = Color.FromArgb("#80000000") };
        var dimTap = new TapGestureRecognizer();
        dimTap.Tapped += (_, _) => CloseDialog();
        dim.GestureRecognizers.Add(dimTap);

        // 吃掉卡片点击，避免点表单关掉弹窗；下拉关闭交给 flyout
        _dialogCard.GestureRecognizers.Add(new TapGestureRecognizer());

        var dialogHost = new Grid
        {
            IsVisible = false,
            Children =
            {
                dim,
                _dialogCard,
                flyoutLayer
            }
        };
        dialogHost.ClassId = "dialogHost";
        dialogHost.SizeChanged += (_, _) =>
        {
            // 随宿主高度收缩，保证矮窗口也能滚到密码/按钮
            var max = Math.Max(280, dialogHost.Height - 48);
            _dialogCard.MaximumHeightRequest = Math.Min(560, max);
        };

        _root = new AbsoluteLayout();
        AbsoluteLayout.SetLayoutBounds(main, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(main, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        AbsoluteLayout.SetLayoutBounds(dialogHost, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(dialogHost, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        _root.Children.Add(main);
        _root.Children.Add(dialogHost);
        Content = _root;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (_service is null)
        {
            SetStatus(Loc.T("Tool.VideoWall.BackendUnavailable"));
            return;
        }

        SetStatus(_service.Backend.IsAvailable
            ? _service.Backend.Name
            : Loc.T("Tool.VideoWall.BackendUnavailable"));
        RefreshList();
    }

    private View BuildHeaderRow()
    {
        return new Border
        {
            BackgroundColor = Color.FromArgb(
                Application.Current?.RequestedTheme == AppTheme.Dark ? "#0F172A" : "#F3F4F6"),
            Padding = new Thickness(12, 10),
            StrokeThickness = 0,
            Content = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(36),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(88)),
                    new ColumnDefinition(new GridLength(1.4, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(72)),
                    new ColumnDefinition(new GridLength(200))
                ],
                ColumnSpacing = 8,
                Children =
                {
                    new Label { Text = "", FontSize = 12 },
                    Header(Loc.T("Tool.VideoWall.ColName")).Also(v => Grid.SetColumn(v, 1)),
                    Header(Loc.T("Tool.VideoWall.ColKind")).Also(v => Grid.SetColumn(v, 2)),
                    Header(Loc.T("Tool.VideoWall.Field.Address")).Also(v => Grid.SetColumn(v, 3)),
                    Header(Loc.T("Tool.VideoWall.ColStatus")).Also(v => Grid.SetColumn(v, 4)),
                    Header(Loc.T("Tool.VideoWall.ColActions")).Also(v => Grid.SetColumn(v, 5))
                }
            }
        };

        Label Header(string t) => new()
        {
            Text = t,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = _muted
        };
    }

    private View BuildDataRow()
    {
        var check = new CheckBox { VerticalOptions = LayoutOptions.Center };
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(SourceRow.IsSelected), BindingMode.TwoWay));
        check.CheckedChanged += (_, _) => UpdateDeleteEnabled();

        var name = new Label { FontSize = 13, TextColor = _text, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(SourceRow.Name));
        var kind = new Label { FontSize = 12, TextColor = _muted };
        kind.SetBinding(Label.TextProperty, nameof(SourceRow.KindText));
        var addr = new Label { FontSize = 12, TextColor = _muted, LineBreakMode = LineBreakMode.TailTruncation };
        addr.SetBinding(Label.TextProperty, nameof(SourceRow.Address));
        var status = new Label { FontSize = 12, TextColor = _muted };
        status.SetBinding(Label.TextProperty, nameof(SourceRow.StatusText));

        var play = MakeRowBtn(Loc.T("Tool.VideoWall.Play"));
        play.SetBinding(Button.ClassIdProperty, nameof(SourceRow.Id));
        play.Clicked += async (s, _) =>
        {
            if (s is Button { ClassId: { Length: > 0 } id })
                await PlayAsync(id);
        };
        var edit = MakeRowBtn(Loc.T("Tool.VideoWall.Editing"));
        edit.SetBinding(Button.ClassIdProperty, nameof(SourceRow.Id));
        edit.Clicked += (s, _) =>
        {
            if (s is Button { ClassId: { Length: > 0 } id })
                OpenDialog(edit: id);
        };
        var del = MakeRowBtn(Loc.T("Tool.VideoWall.Delete"), danger: true);
        del.SetBinding(Button.ClassIdProperty, nameof(SourceRow.Id));
        del.Clicked += async (s, _) =>
        {
            if (s is Button { ClassId: { Length: > 0 } id })
                await DeleteOneAsync(id);
        };

        return new Border
        {
            Padding = new Thickness(12, 8),
            StrokeThickness = 0,
            Content = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(36),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(88)),
                    new ColumnDefinition(new GridLength(1.4, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(72)),
                    new ColumnDefinition(new GridLength(200))
                ],
                ColumnSpacing = 8,
                Children =
                {
                    check,
                    name.Also(v => Grid.SetColumn(v, 1)),
                    kind.Also(v => Grid.SetColumn(v, 2)),
                    addr.Also(v => Grid.SetColumn(v, 3)),
                    status.Also(v => Grid.SetColumn(v, 4)),
                    new HorizontalStackLayout
                    {
                        Spacing = 4,
                        Children = { play, edit, del }
                    }.Also(v => Grid.SetColumn(v, 5))
                }
            }
        };
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_service is null) return;
        _service.CatalogChanged += OnCatalogChanged;
        _service.SinglePlayback.StateChanged += OnPlayState;
        RefreshList();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_service is null) return;
        _service.CatalogChanged -= OnCatalogChanged;
        _service.SinglePlayback.StateChanged -= OnPlayState;
        _ = _service.SinglePlayback.StopAsync();
    }

    private void OnCatalogChanged() => MainThread.BeginInvokeOnMainThread(RefreshList);

    private void OnPlayState(TilePlayState state)
        => MainThread.BeginInvokeOnMainThread(() => SetStatus(StatusText(state)));

    private void RefreshList()
    {
        if (_service is null) return;
        var q = (_search.Text ?? "").Trim();
        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        _rows.Clear();
        foreach (var s in _service.Catalog.Sources)
        {
            if (q.Length > 0
                && !s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !s.Endpoint.Address.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            _rows.Add(new SourceRow
            {
                Id = s.Id,
                Name = s.Name,
                KindText = KindLabel(s.Kind),
                Address = VideoEndpointParser.RedactAddress(s.Endpoint.Address),
                StatusText = ValidationLabel(s.ValidationState),
                IsSelected = selected.Contains(s.Id),
                Source = s
            });
        }

        _empty.IsVisible = _rows.Count == 0;
        _table.IsVisible = _rows.Count > 0;
        _suppressSelectAll = true;
        _selectAll.IsChecked = _rows.Count > 0 && _rows.All(r => r.IsSelected);
        _suppressSelectAll = false;
        UpdateDeleteEnabled();
    }

    private void OpenDialog(string? edit)
    {
        _editingId = edit;
        _formError.IsVisible = false;
        _flyout.Close();
        _dialogTitle.Text = edit is null
            ? Loc.T("Tool.VideoWall.AddSource")
            : Loc.T("Tool.VideoWall.Editing");

        if (edit is not null && _service is not null)
        {
            var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == edit);
            if (src is not null)
            {
                var kinds = ImplementedKinds();
                _kindDrop.SelectedIndex = Math.Max(0, kinds.IndexOf(src.Kind));
                _kindDrop.SetEnabled(false);
                _name.Text = src.Name;
                _address.Text = src.Endpoint.Address;
                _authDrop.SelectedIndex = 0;
                _user.Text = "";
                _pass.Text = "";
            }
        }
        else
        {
            _kindDrop.SetEnabled(true);
            _kindDrop.SelectedIndex = 0;
            _name.Text = "";
            _address.Text = "";
            _authDrop.SelectedIndex = 0;
            _user.Text = "";
            _pass.Text = "";
        }

        SetDialogVisible(true);
    }

    private void CloseDialog()
    {
        _editingId = null;
        _flyout.Close();
        SetDialogVisible(false);
    }

    private void SetDialogVisible(bool visible)
    {
        foreach (var child in _root.Children)
        {
            if (child is Grid { ClassId: "dialogHost" } host)
                host.IsVisible = visible;
        }
    }

    private async Task SaveDialogAsync()
    {
        if (_service is null) return;
        _formError.IsVisible = false;

        var kinds = ImplementedKinds();
        var kindIdx = _kindDrop.SelectedIndex;
        var kind = kindIdx >= 0 && kindIdx < kinds.Count
            ? kinds[kindIdx]
            : VideoSourceKind.Camera;
        if (_editingId is not null)
        {
            var existing = _service.Catalog.Sources.FirstOrDefault(s => s.Id == _editingId);
            if (existing is not null)
                kind = existing.Kind;
        }

        var auth = _authDrop.SelectedIndex switch
        {
            1 => SourceFormSchema.AuthBasic,
            2 => SourceFormSchema.AuthDigest,
            _ => SourceFormSchema.AuthNone
        };

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = _name.Text,
            ["address"] = _address.Text,
            ["path"] = kind == VideoSourceKind.LocalFile ? _address.Text : null,
            ["auth"] = auth,
            ["username"] = _user.Text,
            ["password"] = _pass.Text,
            ["decode"] = "auto",
            ["bufferMs"] = "800",
            ["transport"] = "tcp"
        };

        var descriptor = SourceDescriptorBuilder.TryBuild(kind, values, _service.Credentials, out var err);
        if (descriptor is null)
        {
            _formError.Text = err ?? "无法保存";
            _formError.IsVisible = true;
            return;
        }

        if (_editingId is not null)
            descriptor = descriptor with { Id = _editingId };

        _service.Credentials.Save();
        var result = _editingId is null
            ? await _service.AddSourceAsync(descriptor)
            : await _service.UpdateSourceAsync(descriptor);

        if (!result.Added)
        {
            _formError.Text = result.Reason;
            _formError.IsVisible = true;
            return;
        }

        _ = await _service.RefreshCoverAsync(descriptor);
        CloseDialog();
        RefreshList();
        SetStatus(_editingId is null ? Loc.T("Tool.VideoWall.AddSource") + " OK" : Loc.T("Tool.VideoWall.Editing") + " OK");
    }

    private async Task PlayAsync(string id)
    {
        if (_service is null) return;
        try
        {
            await _service.SinglePlayback.PlaySourceAsync(id);
            _playingId = id;
            SetStatus(Loc.T("Tool.VideoWall.StatusConnecting"));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task DeleteOneAsync(string id)
    {
        if (_service is null) return;
        var page = GetPage();
        if (page is not null)
        {
            var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == id);
            var ok = await page.DisplayAlertAsync(
                Loc.T("Tool.VideoWall.Delete"),
                string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), src?.Name ?? id),
                Loc.T("Tool.VideoWall.Delete"),
                Loc.T("Tool.VideoWall.Cancel"));
            if (!ok) return;
        }

        if (_playingId == id)
            await _service.SinglePlayback.StopAsync();
        await _service.RemoveSourceAsync(id);
        RefreshList();
    }

    private async Task DeleteSelectedAsync()
    {
        if (_service is null) return;
        var ids = _rows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (ids.Count == 0) return;

        var page = GetPage();
        if (page is not null)
        {
            var ok = await page.DisplayAlertAsync(
                Loc.T("Tool.VideoWall.Delete"),
                string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), $"{ids.Count}"),
                Loc.T("Tool.VideoWall.Delete"),
                Loc.T("Tool.VideoWall.Cancel"));
            if (!ok) return;
        }

        foreach (var id in ids)
        {
            if (_playingId == id)
                await _service.SinglePlayback.StopAsync();
            await _service.RemoveSourceAsync(id);
        }

        RefreshList();
    }

    private void UpdateDeleteEnabled()
        => _deleteBtn.IsEnabled = _rows.Any(r => r.IsSelected);

    private void SetStatus(string text)
    {
        _status.Text = text;
        StatusReporter?.Invoke(text);
    }

    private Page? GetPage()
        => Application.Current?.Windows.FirstOrDefault()?.Page
           ?? Parent as Page
           ?? this.FindParentPage();

    private static List<VideoSourceKind> ImplementedKinds()
        => SourceFormSchema.Types
            .Where(x => x.Implemented && x.Kind != VideoSourceKind.NvrChannel)
            .Select(x => x.Kind)
            .ToList();

    private static string KindLabel(VideoSourceKind kind) => kind switch
    {
        VideoSourceKind.Camera => Loc.T("Tool.VideoWall.Type.Camera"),
        VideoSourceKind.NetworkUrl => Loc.T("Tool.VideoWall.Type.Network"),
        VideoSourceKind.LocalFile => Loc.T("Tool.VideoWall.Type.LocalFile"),
        VideoSourceKind.NvrChannel => Loc.T("Tool.VideoWall.Type.Nvr"),
        _ => kind.ToString()
    };

    private static string ValidationLabel(SourceValidationState v) => v switch
    {
        SourceValidationState.Ok => "OK",
        SourceValidationState.Failed => "Failed",
        _ => "—"
    };

    private static string StatusText(TilePlayState state) => state switch
    {
        TilePlayState.Connecting => Loc.T("Tool.VideoWall.StatusConnecting"),
        TilePlayState.Buffering => Loc.T("Tool.VideoWall.StatusBuffering"),
        TilePlayState.Playing => "Playing",
        TilePlayState.Stalled => Loc.T("Tool.VideoWall.StatusStalled"),
        TilePlayState.Reconnecting => Loc.T("Tool.VideoWall.StatusReconnecting"),
        TilePlayState.Failed => Loc.T("Tool.VideoWall.BackendUnavailable"),
        TilePlayState.Stopped => Loc.T("Tool.VideoWall.StatusStopped"),
        _ => state.ToString()
    };

    private Entry MakeEntry(string placeholder)
        => new()
        {
            Placeholder = placeholder,
            TextColor = _text,
            FontSize = 13,
            HeightRequest = 36,
            MinimumHeightRequest = 36,
            HorizontalOptions = LayoutOptions.Fill,
            BackgroundColor = _card
        };

    private View FieldBlock(string label, View control)
        => new VerticalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.Fill,
            Children =
            {
                new Label
                {
                    Text = label,
                    FontSize = 12,
                    TextColor = _muted
                },
                control
            }
        };

    private Button MakeBtn(string text, Color bg, Color fg, Color? border = null)
        => new()
        {
            Text = text,
            BackgroundColor = bg,
            TextColor = fg,
            FontSize = 13,
            Padding = new Thickness(14, 8),
            BorderColor = border ?? Colors.Transparent,
            BorderWidth = border is null ? 0 : 1,
            CornerRadius = 8
        };

    private Button MakeRowBtn(string text, bool danger = false)
        => new()
        {
            Text = text,
            FontSize = 12,
            Padding = new Thickness(8, 4),
            BackgroundColor = _card,
            TextColor = danger ? Color.FromArgb("#DC2626") : _text,
            BorderColor = _line,
            BorderWidth = 1,
            CornerRadius = 6,
            MinimumWidthRequest = 52
        };

    private sealed class SourceRow : INotifyPropertyChanged
    {
        private bool _selected;
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string KindText { get; init; } = "";
        public string Address { get; init; } = "";
        public string StatusText { get; init; } = "";
        public VideoSourceDescriptor? Source { get; init; }

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

file static class VideoWallMauiExt
{
    public static T Also<T>(this T view, Action<T> configure)
    {
        configure(view);
        return view;
    }

    public static Page? FindParentPage(this Element element)
    {
        for (var p = element.Parent; p is not null; p = p.Parent)
        {
            if (p is Page page)
                return page;
        }

        return null;
    }
}
