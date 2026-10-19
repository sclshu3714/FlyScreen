using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi;

public partial class ProgramAddDialog : Window
{
    private bool _nsManual;
    private bool _fileManual;
    private bool _updating;

    public bool Confirmed { get; private set; }
    public string ProgramName => NameBox.Text?.Trim() ?? "";
    public ProgramLanguage SelectedLanguage => CurrentLanguage();
    public string NamespaceValue => NamespaceBox.Text?.Trim() ?? "";
    public string FileNameValue => FileNameBox.Text?.Trim() ?? "";

    public ProgramAddDialog()
    {
        InitializeComponent();
        Title = Loc.T("Prog.DlgAddTitle");
        LblName.Text = Loc.T("Prog.DlgName");
        LblLang.Text = Loc.T("Prog.DlgLang");
        LblNs.Text = Loc.T("Prog.DlgNamespace");
        LblFile.Text = Loc.T("Prog.DlgFileName");
        SaveBtn.Content = Loc.T("Prog.DlgSave");
        CancelBtn.Content = Loc.T("Prog.DlgCancel");
        DetectBtn.Content = Loc.T("Prog.DlgDetect");

        LangCombo.ItemsSource = ProgramLanguageInfo.All
            .Select(l => new LangItem(l))
            .ToList();
        LangCombo.SelectedIndex = 0;
        NamespaceBox.GotFocus += (_, _) => _nsManual = true;
        FileNameBox.GotFocus += (_, _) => _fileManual = true;
        SetNs(ChinesePinyin.ToNamespace(""));
        SetFile(ProgramLanguageInfo.DefaultFileName(ProgramLanguage.Python));
    }

    private void NameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || _nsManual) return;
        SetNs(ChinesePinyin.ToNamespace(NameBox.Text));
    }

    private void LangCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var lang = CurrentLanguage();
        if (_fileManual)
        {
            var baseName = Path.GetFileNameWithoutExtension(FileNameBox.Text);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "main";
            SetFile(baseName + ProgramLanguageInfo.Extension(lang));
        }
        else
        {
            SetFile(ProgramLanguageInfo.DefaultFileName(lang));
        }
    }

    private void SetNs(string value)
    {
        _updating = true;
        NamespaceBox.Text = value;
        _updating = false;
    }

    private void SetFile(string value)
    {
        _updating = true;
        FileNameBox.Text = value;
        _updating = false;
    }

    private ProgramLanguage CurrentLanguage()
        => LangCombo.SelectedItem is LangItem item ? item.Language : ProgramLanguage.Python;

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            StatusTip();
            return;
        }
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private async void Detect_Click(object? sender, RoutedEventArgs e)
    {
        var lang = CurrentLanguage();
        DetectBtn.IsEnabled = false;
        DetectStatus.Text = Loc.T("Prog.DetectChecking");
        DetectStatus.Foreground = Brushes.Gray;
        try
        {
            var result = await Task.Run(() => ProgramEnvironmentChecker.Check(lang));
            if (result.Ok)
            {
                DetectStatus.Text = Loc.T("Prog.DetectOk");
                DetectStatus.Foreground = Brushes.SeaGreen;
                await ShowInfoAsync(Loc.T("Prog.DetectOk"), result.Detail);
                return;
            }

            DetectStatus.Text = Loc.T("Prog.DetectFail");
            DetectStatus.Foreground = Brushes.DarkOrange;
            var ask = string.Format(Loc.T("Prog.DetectAsk"), result.Detail);
            var choice = await ShowDetectChoiceAsync(Loc.T("Prog.DetectFail"), ask);
            if (choice == "auto")
            {
                DetectStatus.Text = Loc.T("Prog.DetectAuto") + "…";
                var (ok, msg) = await ProgramEnvironmentChecker.TryAutoInstallAsync(lang, result);
                DetectStatus.Text = ok ? Loc.T("Prog.DetectOk") : Loc.T("Prog.DetectFail");
                DetectStatus.Foreground = ok ? Brushes.SeaGreen : Brushes.DarkOrange;
                await ShowInfoAsync(Title ?? "玄鸟", msg);
            }
            else if (choice == "open")
            {
                ProgramEnvironmentChecker.OpenInstallPage(result);
            }
        }
        finally
        {
            DetectBtn.IsEnabled = true;
        }
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var win = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var ok = new Button { Content = "OK", Width = 88, Height = 32, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => win.Close();
        var buttonRow = new StackPanel { Children = { ok } };
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        win.Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children = { buttonRow, body }
        };
        await win.ShowDialog(this);
    }

    private async Task<string?> ShowDetectChoiceAsync(string title, string message)
    {
        string? choice = null;
        var win = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var auto = new Button { Content = Loc.T("Prog.DetectAuto"), MinWidth = 96, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        var open = new Button { Content = Loc.T("Prog.DetectOpen"), MinWidth = 96, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.T("Prog.DlgCancel"), MinWidth = 88, Height = 32 };
        auto.Click += (_, _) => { choice = "auto"; win.Close(); };
        open.Click += (_, _) => { choice = "open"; win.Close(); };
        cancel.Click += (_, _) => { choice = null; win.Close(); };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { auto, open, cancel }
        };
        win.Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                buttons,
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) }
            }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        await win.ShowDialog(this);
        return choice;
    }

    private void StatusTip()
    {
        Title = Loc.T("Prog.DlgName") + " 不能为空";
    }

    private sealed class LangItem
    {
        public LangItem(ProgramLanguage language) => Language = language;
        public ProgramLanguage Language { get; }
        public override string ToString() => ProgramLanguageInfo.DisplayName(Language);
    }
}
