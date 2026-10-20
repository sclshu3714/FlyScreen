using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi;

public partial class ProgramAddDialog : Window
{
    private bool _nsManual;
    private bool _fileManual;
    private bool _updating;
    private LinuxClientChrome? _chrome;
    private ProgramEnvCheckResult? _lastFail;

    public bool Confirmed { get; private set; }
    public string ProgramName => NameBox.Text?.Trim() ?? "";
    public ProgramLanguage SelectedLanguage => CurrentLanguage();
    public string NamespaceValue => NamespaceBox.Text?.Trim() ?? "";
    public string FileNameValue => FileNameBox.Text?.Trim() ?? "";

    public ProgramAddDialog()
    {
        InitializeComponent();
        var addTitle = Loc.T("Prog.DlgAddTitle");
        _chrome = LinuxClientChrome.Attach(
            this, ClientTitleBar, ClientTitleText, addTitle,
            closeBtn: TitleCloseBtn, allowMaximize: false);

        if (Application.Current?.Resources.TryGetResource("UiFont", null, out var font) == true
            && font is FontFamily ff)
            FontFamily = ff;

        LblName.Text = Loc.T("Prog.DlgName");
        LblLang.Text = Loc.T("Prog.DlgLang");
        LblNs.Text = Loc.T("Prog.DlgNamespace");
        LblFile.Text = Loc.T("Prog.DlgFileName");
        SaveBtn.Content = Loc.T("Prog.DlgSave");
        CancelBtn.Content = Loc.T("Prog.DlgCancel");
        DetectBtn.Content = Loc.T("Prog.DlgDetect");
        DetectAutoBtn.Content = Loc.T("Prog.DetectAuto");
        DetectOpenBtn.Content = Loc.T("Prog.DetectOpen");

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
        ClearDetectUi();
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
        DetectActions.IsVisible = false;
        _lastFail = null;
        SetDetectStatus(Loc.T("Prog.DetectChecking"), Brushes.Gray);
        try
        {
            var result = await Task.Run(() => ProgramEnvironmentChecker.Check(lang));
            if (result.Ok)
            {
                SetDetectStatus($"{Loc.T("Prog.DetectOk")} — {result.Detail}", Brushes.SeaGreen);
                return;
            }

            _lastFail = result;
            SetDetectStatus($"{Loc.T("Prog.DetectFail")} — {result.Detail}", Brushes.DarkOrange);
            DetectActions.IsVisible = true;
        }
        finally
        {
            DetectBtn.IsEnabled = true;
        }
    }

    private async void DetectAuto_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastFail is null) return;
        var lang = CurrentLanguage();
        DetectAutoBtn.IsEnabled = DetectOpenBtn.IsEnabled = false;
        SetDetectStatus(Loc.T("Prog.DetectAuto") + "…", Brushes.Gray);
        try
        {
            var (ok, msg) = await ProgramEnvironmentChecker.TryAutoInstallAsync(lang, _lastFail);
            SetDetectStatus(ok ? $"{Loc.T("Prog.DetectOk")} — {msg}" : $"{Loc.T("Prog.DetectFail")} — {msg}",
                ok ? Brushes.SeaGreen : Brushes.DarkOrange);
            DetectActions.IsVisible = !ok;
            if (!ok && !string.IsNullOrWhiteSpace(_lastFail.InstallUrl))
                ProgramEnvironmentChecker.OpenInstallPage(_lastFail);
            if (ok) _lastFail = null;
        }
        finally
        {
            DetectAutoBtn.IsEnabled = DetectOpenBtn.IsEnabled = true;
        }
    }

    private void DetectOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastFail is null) return;
        var guide = ProgramEnvironmentChecker.BuildManualRepairGuide(CurrentLanguage(), _lastFail);
        SetDetectStatus($"{Loc.T("Prog.DetectManualTitle")}\n{guide}", Brushes.DarkOrange);
        if (!string.IsNullOrWhiteSpace(_lastFail.InstallUrl))
            ProgramEnvironmentChecker.OpenInstallPage(_lastFail);
    }

    private void SetDetectStatus(string text, IBrush brush)
    {
        DetectStatus.Text = text;
        DetectStatus.Foreground = brush;
    }

    private void ClearDetectUi()
    {
        DetectStatus.Text = "";
        DetectActions.IsVisible = false;
        _lastFail = null;
    }

    private void StatusTip()
    {
        var tip = Loc.T("Prog.DlgName") + " 不能为空";
        _chrome?.SetTitle(tip);
        if (_chrome == null)
            Title = tip;
    }

    private sealed class LangItem
    {
        public LangItem(ProgramLanguage language) => Language = language;
        public ProgramLanguage Language { get; }
        public override string ToString() => ProgramLanguageInfo.DisplayName(Language);
    }
}
