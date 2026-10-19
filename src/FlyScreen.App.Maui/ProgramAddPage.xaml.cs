using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen.AppMaui;

public partial class ProgramAddPage : ContentPage
{
    private bool _nsManual;
    private bool _fileManual;
    private readonly TaskCompletionSource<bool> _tcs = new();

    public string ProgramName => NameBox.Text?.Trim() ?? "";
    public ProgramLanguage SelectedLanguage =>
        LangPicker.SelectedIndex >= 0 && LangPicker.SelectedIndex < ProgramLanguageInfo.All.Count
            ? ProgramLanguageInfo.All[LangPicker.SelectedIndex]
            : ProgramLanguage.Python;
    public string NamespaceValue => NamespaceBox.Text?.Trim() ?? "";
    public string FileNameValue => FileNameBox.Text?.Trim() ?? "";
    public Task<bool> Result => _tcs.Task;

    public ProgramAddPage()
    {
        InitializeComponent();
        Title = Loc.T("Prog.DlgAddTitle");
        LblName.Text = Loc.T("Prog.DlgName");
        LblLang.Text = Loc.T("Prog.DlgLang");
        LblNs.Text = Loc.T("Prog.DlgNamespace");
        LblFile.Text = Loc.T("Prog.DlgFileName");
        SaveBtn.Text = Loc.T("Prog.DlgSave");
        CancelBtn.Text = Loc.T("Prog.DlgCancel");

        LangPicker.ItemsSource = ProgramLanguageInfo.All
            .Select(ProgramLanguageInfo.DisplayName)
            .ToList();
        LangPicker.SelectedIndex = 0;
        NamespaceBox.Focused += (_, _) => _nsManual = true;
        FileNameBox.Focused += (_, _) => _fileManual = true;
        NamespaceBox.Text = ChinesePinyin.ToNamespace("");
        FileNameBox.Text = ProgramLanguageInfo.DefaultFileName(ProgramLanguage.Python);
    }

    private void NameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_nsManual)
            NamespaceBox.Text = ChinesePinyin.ToNamespace(NameBox.Text);
    }

    private void LangPicker_Changed(object? sender, EventArgs e)
    {
        var lang = SelectedLanguage;
        if (_fileManual)
        {
            var baseName = Path.GetFileNameWithoutExtension(FileNameBox.Text);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "main";
            FileNameBox.Text = baseName + ProgramLanguageInfo.Extension(lang);
        }
        else
        {
            FileNameBox.Text = ProgramLanguageInfo.DefaultFileName(lang);
        }
    }

    private async void Save_Clicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            await DisplayAlertAsync(Title, Loc.T("Prog.DlgName") + " 不能为空", "OK");
            return;
        }
        _tcs.TrySetResult(true);
        await Navigation.PopModalAsync();
    }

    private async void Cancel_Clicked(object? sender, EventArgs e)
    {
        _tcs.TrySetResult(false);
        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tcs.TrySetResult(false);
    }
}
