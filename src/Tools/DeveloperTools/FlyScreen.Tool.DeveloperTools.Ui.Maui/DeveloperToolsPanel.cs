using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.DeveloperTools.Scaffold;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.DeveloperTools.Ui.Maui;

/// <summary>开发者工具工作区：填写信息 → 生成脚手架 → 打开 IDE。</summary>
public sealed class DeveloperToolsPanel : ContentView
{
    private readonly IDeveloperToolsService _service;
    private readonly Entry _nameZh;
    private readonly Entry _nameEn;
    private readonly Editor _summary;
    private readonly CheckBox _desktop;
    private readonly CheckBox _phone;
    private readonly Picker _uiMode;
    private readonly Entry _output;
    private readonly CheckBox _register;
    private readonly Label _status;
    private readonly Button _generate;
    private readonly HorizontalStackLayout _openRow;
    private readonly Button _openVs;
    private readonly Button _openCode;
    private readonly Button _openFolder;

    public Action<string>? StatusReporter { get; set; }

    public DeveloperToolsPanel()
    {
        _service = ToolService.Instance.GetToolService<IDeveloperToolsService>()
                   ?? ToolService.Instance.GetToolService<DeveloperToolsService>()
                   ?? throw new InvalidOperationException("开发者工具未安装或未加载");

        var dark = ToolMauiTheme.IsDark;
        var text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        var muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        var line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        var card = Color.FromArgb(dark ? "#0F172A" : "#F8FAFC");
        var blue = Color.FromArgb("#2563EB");

        _nameZh = MakeEntry(_service.Draft.DisplayNameZh);
        _nameEn = MakeEntry(_service.Draft.ProjectNameEn);
        _summary = new Editor
        {
            AutoSize = EditorAutoSizeOption.TextChanges,
            HeightRequest = 72,
            Text = _service.Draft.SummaryZh,
            TextColor = text
        };
        _desktop = new CheckBox { IsChecked = _service.Draft.SupportDesktop, VerticalOptions = LayoutOptions.Center };
        _phone = new CheckBox { IsChecked = _service.Draft.SupportPhone, VerticalOptions = LayoutOptions.Center };
        _uiMode = new Picker
        {
            ItemsSource = new List<string>
            {
                Loc.T("Tool.DeveloperTools.UiWorkspace"),
                Loc.T("Tool.DeveloperTools.UiDialog"),
                Loc.T("Tool.DeveloperTools.UiBackground")
            },
            SelectedIndex = (int)_service.Draft.UiMode,
            TextColor = text
        };
        _output = MakeEntry(_service.Draft.OutputRoot);
        _register = new CheckBox { IsChecked = _service.Draft.RegisterInRepo, VerticalOptions = LayoutOptions.Center };
        _status = new Label { FontSize = 12, TextColor = muted, LineBreakMode = LineBreakMode.WordWrap };
        _generate = MakeBtn(Loc.T("Tool.DeveloperTools.Generate"), blue, Colors.White);
        _generate.Clicked += async (_, _) => await GenerateAsync();

        _openVs = MakeBtn(Loc.T("Tool.DeveloperTools.OpenVs"), card, text, line);
        _openCode = MakeBtn(Loc.T("Tool.DeveloperTools.OpenCode"), card, text, line);
        _openFolder = MakeBtn(Loc.T("Tool.DeveloperTools.OpenFolder"), card, text, line);
        _openVs.Clicked += (_, _) => OpenIde(IdeKind.Vs);
        _openCode.Clicked += (_, _) => OpenIde(IdeKind.Code);
        _openFolder.Clicked += (_, _) => OpenIde(IdeKind.Folder);
        _openRow = new HorizontalStackLayout
        {
            Spacing = 8,
            IsVisible = false,
            Children = { _openVs, _openCode, _openFolder }
        };

        var browse = MakeBtn(Loc.T("Tool.DeveloperTools.Browse"), card, text, line);
        browse.Clicked += async (_, _) => await BrowseAsync();

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children =
                {
                    new Label
                    {
                        Text = Loc.T("Tool.DeveloperTools.Hint"),
                        FontSize = 12,
                        TextColor = muted,
                        LineBreakMode = LineBreakMode.WordWrap
                    },
                    Labeled(Loc.T("Tool.DeveloperTools.DisplayNameZh"), text, muted, _nameZh),
                    Labeled(Loc.T("Tool.DeveloperTools.ProjectNameEn"), text, muted, _nameEn),
                    Labeled(Loc.T("Tool.DeveloperTools.SummaryLabel"), text, muted, _summary),
                    new Label { Text = Loc.T("Tool.DeveloperTools.Platforms"), FontSize = 12, TextColor = muted },
                    new HorizontalStackLayout
                    {
                        Spacing = 16,
                        Children =
                        {
                            new HorizontalStackLayout
                            {
                                Spacing = 6,
                                Children =
                                {
                                    _desktop,
                                    new Label { Text = Loc.T("Tool.DeveloperTools.Desktop"), TextColor = text, VerticalOptions = LayoutOptions.Center }
                                }
                            },
                            new HorizontalStackLayout
                            {
                                Spacing = 6,
                                Children =
                                {
                                    _phone,
                                    new Label { Text = Loc.T("Tool.DeveloperTools.Phone"), TextColor = text, VerticalOptions = LayoutOptions.Center }
                                }
                            }
                        }
                    },
                    Labeled(Loc.T("Tool.DeveloperTools.UiMode"), text, muted, _uiMode),
                    new Label { Text = Loc.T("Tool.DeveloperTools.OutputRoot"), FontSize = 12, TextColor = muted },
                    BuildOutputRow(_output, browse),
                    new HorizontalStackLayout
                    {
                        Spacing = 6,
                        Children =
                        {
                            _register,
                            new Label
                            {
                                Text = Loc.T("Tool.DeveloperTools.RegisterRepo"),
                                TextColor = text,
                                VerticalOptions = LayoutOptions.Center,
                                LineBreakMode = LineBreakMode.WordWrap
                            }
                        }
                    },
                    _generate,
                    _openRow,
                    _status
                }
            }
        };

        RefreshOpenRow();
    }

    private Task BrowseAsync()
    {
#if WINDOWS
        var path = DevToolsWin32FolderPicker.PickFolder(Loc.T("Tool.DeveloperTools.OutputRoot"));
        if (!string.IsNullOrWhiteSpace(path))
            _output.Text = path;
#else
        SetStatus("请手动填写输出目录路径");
#endif
        return Task.CompletedTask;
    }

    private async Task GenerateAsync()
    {
        ApplyDraft();
        _generate.IsEnabled = false;
        SetStatus(Loc.T("Tool.DeveloperTools.Generating"));
        try
        {
            var result = await Task.Run(() => _service.Generate()).ConfigureAwait(true);
            SetStatus(result.Ok
                ? Loc.T("Tool.DeveloperTools.Done") + "\n" + Loc.T("Tool.DeveloperTools.OpenHint") + "\n" + result.Message
                : Loc.T("Tool.DeveloperTools.Failed") + "\n" + result.Message);
            RefreshOpenRow();
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Tool.DeveloperTools.Failed") + "\n" + ex.Message);
        }
        finally
        {
            _generate.IsEnabled = true;
        }
    }

    private void ApplyDraft()
    {
        var d = _service.Draft;
        d.DisplayNameZh = _nameZh.Text?.Trim() ?? "";
        d.ProjectNameEn = _nameEn.Text?.Trim() ?? "";
        d.SummaryZh = _summary.Text?.Trim() ?? "";
        d.SupportDesktop = _desktop.IsChecked;
        d.SupportPhone = _phone.IsChecked;
        d.UiMode = _uiMode.SelectedIndex switch
        {
            1 => ToolUiMode.Dialog,
            2 => ToolUiMode.Background,
            _ => ToolUiMode.Workspace
        };
        d.OutputRoot = _output.Text?.Trim() ?? "";
        d.RegisterInRepo = _register.IsChecked;
    }

    private void RefreshOpenRow()
    {
        var folder = _service.LastResult?.ProjectFolder;
        var ok = _service.LastResult?.Ok == true && !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        _openRow.IsVisible = ok;
        _openVs.IsVisible = OperatingSystem.IsWindows();
    }

    private void OpenIde(IdeKind kind)
    {
        var folder = _service.LastResult?.ProjectFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;
        var ok = kind switch
        {
            IdeKind.Vs => IdeLauncher.TryOpenVisualStudio(folder, out var e1) || Fail(e1),
            IdeKind.Code => IdeLauncher.TryOpenVsCode(folder, out var e2) || Fail(e2),
            _ => IdeLauncher.TryOpenFolder(folder, out var e3) || Fail(e3)
        };
        if (ok) SetStatus(folder);

        bool Fail(string err)
        {
            SetStatus(err);
            return false;
        }
    }

    private void SetStatus(string msg)
    {
        _status.Text = msg;
        StatusReporter?.Invoke(msg.Replace('\n', ' '));
    }

    private static Entry MakeEntry(string text) => new() { Text = text, FontSize = 14 };

    private static Button MakeBtn(string text, Color bg, Color fg, Color? border = null)
    {
        var b = new Button
        {
            Text = text,
            BackgroundColor = bg,
            TextColor = fg,
            Padding = new Thickness(12, 8),
            HorizontalOptions = LayoutOptions.Start
        };
        if (border != null)
        {
            b.BorderColor = border;
            b.BorderWidth = 1;
        }
        return b;
    }

    private static View Labeled(string label, Color text, Color muted, View child)
        => new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = label, FontSize = 12, TextColor = muted },
                child
            }
        };

    private static Grid BuildOutputRow(Entry output, Button browse)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto)
            },
            ColumnSpacing = 8
        };
        grid.Add(output, 0, 0);
        grid.Add(browse, 1, 0);
        return grid;
    }

    private enum IdeKind { Vs, Code, Folder }
}
