using System.Windows;
using System.Windows.Controls;
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
        Title = Loc.T("Prog.DlgAddTitle");
        LblName.Text = Loc.T("Prog.DlgName");
        LblLang.Text = Loc.T("Prog.DlgLang");
        LblNs.Text = Loc.T("Prog.DlgNamespace");
        LblFile.Text = Loc.T("Prog.DlgFileName");
        SaveBtn.Content = Loc.T("Prog.DlgSave");
        CancelBtn.Content = Loc.T("Prog.DlgCancel");

        foreach (var lang in ProgramLanguageInfo.All)
            LangCombo.Items.Add(new LangItem(lang));
        LangCombo.DisplayMemberPath = nameof(LangItem.Display);
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

    private sealed class LangItem
    {
        public LangItem(ProgramLanguage language) => Language = language;
        public ProgramLanguage Language { get; }
        public string Display => ProgramLanguageInfo.DisplayName(Language);
    }
}
