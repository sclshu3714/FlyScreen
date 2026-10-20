using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen;

public partial class ProgramAddDialog : Window
{
    private bool _nsManual;
    private bool _fileManual;
    private bool _updating;
    private ProgramEnvCheckResult? _lastFail;

    public string ProgramName => NameBox.Text.Trim();
    public ProgramLanguage SelectedLanguage => CurrentLanguage();
    public string NamespaceValue => NamespaceBox.Text.Trim();
    public string FileNameValue => FileNameBox.Text.Trim();

    public ProgramAddDialog()
    {
        InitializeComponent();
        ThemeService.ApplyToChildWindow(this);
        Loaded += (_, _) => ThemeService.ApplyToChildWindow(this);
        Title = Loc.T("Prog.DlgAddTitle");
        LblName.Text = Loc.T("Prog.DlgName");
        LblLang.Text = Loc.T("Prog.DlgLang");
        LblNs.Text = Loc.T("Prog.DlgNamespace");
        LblFile.Text = Loc.T("Prog.DlgFileName");
        SaveBtn.Content = Loc.T("Prog.DlgSave");
        CancelBtn.Content = Loc.T("Prog.DlgCancel");
        DetectBtn.Content = Loc.T("Prog.DlgDetect");
        DetectAutoBtn.Content = Loc.T("Prog.DetectAuto");
        DetectOpenBtn.Content = Loc.T("Prog.DetectOpen");

        foreach (var lang in ProgramLanguageInfo.All)
            LangCombo.Items.Add(new LangItem(lang));
        LangCombo.SelectedIndex = 0;

        NamespaceBox.GotFocus += (_, _) => _nsManual = true;
        FileNameBox.GotFocus += (_, _) => _fileManual = true;
        UpdateDerivedFields();
        NameBox.Focus();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (!_nsManual)
            SetNs(ChinesePinyin.ToNamespace(NameBox.Text));
    }

    private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || !IsLoaded) return;
        var lang = CurrentLanguage();
        if (_fileManual)
        {
            var baseName = System.IO.Path.GetFileNameWithoutExtension(FileNameBox.Text);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "main";
            SetFile(baseName + ProgramLanguageInfo.Extension(lang));
        }
        else
        {
            SetFile(ProgramLanguageInfo.DefaultFileName(lang));
        }
        ClearDetectUi();
    }

    private void UpdateDerivedFields()
    {
        SetNs(ChinesePinyin.ToNamespace(NameBox.Text));
        SetFile(ProgramLanguageInfo.DefaultFileName(CurrentLanguage()));
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, Loc.T("Prog.DlgName") + " 不能为空", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        var lang = CurrentLanguage();
        DetectBtn.IsEnabled = false;
        DetectActions.Visibility = Visibility.Collapsed;
        _lastFail = null;
        SetDetectStatus(Loc.T("Prog.DetectChecking"), "Muted");
        try
        {
            await Task.Yield();
            var result = await Task.Run(() => ProgramEnvironmentChecker.Check(lang));
            if (result.Ok)
            {
                SetDetectStatus($"{Loc.T("Prog.DetectOk")} — {result.Detail}", "Success");
                return;
            }

            _lastFail = result;
            SetDetectStatus($"{Loc.T("Prog.DetectFail")} — {result.Detail}", "Warn");
            DetectActions.Visibility = Visibility.Visible;
        }
        finally
        {
            DetectBtn.IsEnabled = true;
        }
    }

    private async void DetectAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFail is null) return;
        var lang = CurrentLanguage();
        DetectAutoBtn.IsEnabled = DetectOpenBtn.IsEnabled = false;
        SetDetectStatus(Loc.T("Prog.DetectAuto") + "…", "Muted");
        try
        {
            var (ok, msg) = await ProgramEnvironmentChecker.TryAutoInstallAsync(lang, _lastFail);
            SetDetectStatus(ok ? $"{Loc.T("Prog.DetectOk")} — {msg}" : $"{Loc.T("Prog.DetectFail")} — {msg}",
                ok ? "Success" : "Warn");
            DetectActions.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok && !string.IsNullOrWhiteSpace(_lastFail.InstallUrl))
                ProgramEnvironmentChecker.OpenInstallPage(_lastFail);
            if (ok) _lastFail = null;
        }
        finally
        {
            DetectAutoBtn.IsEnabled = DetectOpenBtn.IsEnabled = true;
        }
    }

    private void DetectOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFail is null) return;
        var guide = ProgramEnvironmentChecker.BuildManualRepairGuide(CurrentLanguage(), _lastFail);
        SetDetectStatus($"{Loc.T("Prog.DetectManualTitle")}\n{guide}", "Warn");
        if (!string.IsNullOrWhiteSpace(_lastFail.InstallUrl))
            ProgramEnvironmentChecker.OpenInstallPage(_lastFail);
    }

    private void SetDetectStatus(string text, string brushKey)
    {
        DetectStatus.Text = text;
        DetectStatus.Foreground = TryBrush(brushKey) ?? (System.Windows.Media.Brush)FindResource("Muted");
    }

    private System.Windows.Media.Brush? TryBrush(string key)
    {
        try { return FindResource(key) as System.Windows.Media.Brush; }
        catch { return null; }
    }

    private void ClearDetectUi()
    {
        DetectStatus.Text = "";
        DetectActions.Visibility = Visibility.Collapsed;
        _lastFail = null;
    }

    internal sealed class LangItem
    {
        public LangItem(ProgramLanguage language) => Language = language;
        public ProgramLanguage Language { get; }
        public string Display => ProgramLanguageInfo.DisplayName(Language);
        public override string ToString() => Display;
    }
}
