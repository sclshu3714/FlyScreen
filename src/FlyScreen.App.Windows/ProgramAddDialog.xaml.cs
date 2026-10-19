using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen;

public partial class ProgramAddDialog : Window
{
    private bool _nsManual;
    private bool _fileManual;
    private bool _updating;

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
        DetectStatus.Text = Loc.T("Prog.DetectChecking");
        DetectStatus.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
        try
        {
            await Task.Run(() => { }); // 让 UI 先刷新
            var result = await Task.Run(() => ProgramEnvironmentChecker.Check(lang));
            if (result.Ok)
            {
                DetectStatus.Text = Loc.T("Prog.DetectOk");
                DetectStatus.Foreground = (System.Windows.Media.Brush)FindResource("Success");
                MessageBox.Show(this, result.Detail, Loc.T("Prog.DetectOk"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DetectStatus.Text = Loc.T("Prog.DetectFail");
            DetectStatus.Foreground = (System.Windows.Media.Brush)FindResource("Warn");
            var ask = string.Format(Loc.T("Prog.DetectAsk"), result.Detail);
            var choice = MessageBox.Show(this, ask, Loc.T("Prog.DetectFail"),
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            // Yes=自动安装 No=打开安装页 Cancel=关闭
            if (choice == MessageBoxResult.Yes)
            {
                DetectStatus.Text = Loc.T("Prog.DetectAuto") + "…";
                var (ok, msg) = await ProgramEnvironmentChecker.TryAutoInstallAsync(lang, result);
                DetectStatus.Text = ok ? Loc.T("Prog.DetectOk") : Loc.T("Prog.DetectFail");
                DetectStatus.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Success" : "Warn");
                MessageBox.Show(this, msg, Title, MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            else if (choice == MessageBoxResult.No)
            {
                ProgramEnvironmentChecker.OpenInstallPage(result);
            }
        }
        finally
        {
            DetectBtn.IsEnabled = true;
        }
    }

    internal sealed class LangItem
    {
        public LangItem(ProgramLanguage language) => Language = language;
        public ProgramLanguage Language { get; }
        public string Display => ProgramLanguageInfo.DisplayName(Language);
        public override string ToString() => Display;
    }
}
