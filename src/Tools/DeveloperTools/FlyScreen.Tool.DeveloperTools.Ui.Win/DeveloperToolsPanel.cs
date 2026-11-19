using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.DeveloperTools.Scaffold;
using FlyScreen.ToolSdk;
using Microsoft.Win32;

namespace FlyScreen.Tool.DeveloperTools.Ui.Win;

public sealed class DeveloperToolsPanel : UserControl
{
    private readonly IDeveloperToolsService _service;
    private readonly TextBox _nameZh;
    private readonly TextBox _nameEn;
    private readonly TextBox _summary;
    private readonly CheckBox _desktop;
    private readonly CheckBox _phone;
    private readonly ComboBox _uiMode;
    private readonly TextBox _output;
    private readonly CheckBox _register;
    private readonly TextBlock _status;
    private readonly Button _generate;
    private readonly StackPanel _openRow;

    public Action<string>? StatusReporter { get; set; }

    public DeveloperToolsPanel()
    {
        _service = ToolService.Instance.GetToolService<IDeveloperToolsService>()
                   ?? ToolService.Instance.GetToolService<DeveloperToolsService>()
                   ?? throw new InvalidOperationException("开发者工具未安装或未加载");

        _nameZh = new TextBox { Text = _service.Draft.DisplayNameZh };
        _nameEn = new TextBox { Text = _service.Draft.ProjectNameEn };
        _summary = new TextBox
        {
            Text = _service.Draft.SummaryZh,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 72,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _desktop = new CheckBox { Content = Loc.T("Tool.DeveloperTools.Desktop"), IsChecked = _service.Draft.SupportDesktop, Margin = new Thickness(0, 0, 16, 0) };
        _phone = new CheckBox { Content = Loc.T("Tool.DeveloperTools.Phone"), IsChecked = _service.Draft.SupportPhone };
        _uiMode = new ComboBox
        {
            ItemsSource = new[]
            {
                Loc.T("Tool.DeveloperTools.UiWorkspace"),
                Loc.T("Tool.DeveloperTools.UiDialog"),
                Loc.T("Tool.DeveloperTools.UiBackground")
            },
            SelectedIndex = (int)_service.Draft.UiMode,
            MinWidth = 240
        };
        _output = new TextBox { Text = _service.Draft.OutputRoot };
        _register = new CheckBox
        {
            Content = Loc.T("Tool.DeveloperTools.RegisterRepo"),
            IsChecked = _service.Draft.RegisterInRepo,
            Margin = new Thickness(0, 4, 0, 0)
        };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 8, 0, 0) };
        _generate = new Button { Content = Loc.T("Tool.DeveloperTools.Generate"), Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Left };
        _generate.Click += async (_, _) => await GenerateAsync();

        var browse = new Button { Content = Loc.T("Tool.DeveloperTools.Browse"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        browse.Click += (_, _) => Browse();

        var openVs = MakeOpenBtn(Loc.T("Tool.DeveloperTools.OpenVs"), () => Open(IdeKind.Vs));
        var openCode = MakeOpenBtn(Loc.T("Tool.DeveloperTools.OpenCode"), () => Open(IdeKind.Code));
        var openFolder = MakeOpenBtn(Loc.T("Tool.DeveloperTools.OpenFolder"), () => Open(IdeKind.Folder));
        _openRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { openVs, openCode, openFolder }
        };

        var outputRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        outputRow.Children.Add(browse);
        outputRow.Children.Add(_output);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Tool.DeveloperTools.Hint"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(Label(Loc.T("Tool.DeveloperTools.DisplayNameZh")));
        root.Children.Add(_nameZh);
        root.Children.Add(Label(Loc.T("Tool.DeveloperTools.ProjectNameEn")));
        root.Children.Add(_nameEn);
        root.Children.Add(Label(Loc.T("Tool.DeveloperTools.SummaryLabel")));
        root.Children.Add(_summary);
        root.Children.Add(Label(Loc.T("Tool.DeveloperTools.Platforms")));
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _desktop, _phone } });
        root.Children.Add(Label(Loc.T("Tool.DeveloperTools.UiMode")));
        root.Children.Add(_uiMode);
        root.Children.Add(Label(Loc.T("Tool.DeveloperTools.OutputRoot")));
        root.Children.Add(outputRow);
        root.Children.Add(_register);
        root.Children.Add(new Border { Height = 10 });
        root.Children.Add(_generate);
        root.Children.Add(_openRow);
        root.Children.Add(_status);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };

        RefreshOpenRow();
    }

    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Tool.DeveloperTools.OutputRoot") };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            _output.Text = dlg.FolderName;
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
        d.SupportDesktop = _desktop.IsChecked == true;
        d.SupportPhone = _phone.IsChecked == true;
        d.UiMode = _uiMode.SelectedIndex switch
        {
            1 => ToolUiMode.Dialog,
            2 => ToolUiMode.Background,
            _ => ToolUiMode.Workspace
        };
        d.OutputRoot = _output.Text?.Trim() ?? "";
        d.RegisterInRepo = _register.IsChecked == true;
    }

    private void RefreshOpenRow()
    {
        var folder = _service.LastResult?.ProjectFolder;
        var ok = _service.LastResult?.Ok == true && !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        _openRow.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Open(IdeKind kind)
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

    private static TextBlock Label(string text)
        => new() { Text = text, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 2), FontSize = 12 };

    private static Button MakeOpenBtn(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        b.Click += (_, _) => click();
        return b;
    }

    private enum IdeKind { Vs, Code, Folder }
}
