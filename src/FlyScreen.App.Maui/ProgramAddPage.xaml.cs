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
        DetectBtn.Text = Loc.T("Prog.DlgDetect");

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

    private async void Detect_Clicked(object? sender, EventArgs e)
    {
        var lang = SelectedLanguage;
        DetectBtn.IsEnabled = false;
        DetectStatus.Text = Loc.T("Prog.DetectChecking");
        DetectStatus.TextColor = Color.FromArgb("#6B7280");
        try
        {
            var result = await Task.Run(() => ProgramEnvironmentChecker.Check(lang));
            if (result.Ok)
            {
                DetectStatus.Text = Loc.T("Prog.DetectOk") + " — " + result.Detail;
                DetectStatus.TextColor = Color.FromArgb("#059669");
                await DisplayAlertAsync(Loc.T("Prog.DetectOk"), result.Detail, "OK");
                return;
            }

            DetectStatus.Text = Loc.T("Prog.DetectFail");
            DetectStatus.TextColor = Color.FromArgb("#D97706");
            var action = await DisplayActionSheetAsync(
                Loc.T("Prog.DetectFail") + "\n" + result.Detail,
                Loc.T("Prog.DlgCancel"),
                null,
                Loc.T("Prog.DetectAuto"),
                Loc.T("Prog.DetectOpen"));
            if (action == Loc.T("Prog.DetectAuto"))
            {
                DetectStatus.Text = Loc.T("Prog.DetectAuto") + "…";
                var (ok, msg) = await ProgramEnvironmentChecker.TryAutoInstallAsync(lang, result);
                DetectStatus.Text = ok ? Loc.T("Prog.DetectOk") : Loc.T("Prog.DetectFail");
                DetectStatus.TextColor = Color.FromArgb(ok ? "#059669" : "#D97706");
                await DisplayAlertAsync(Title, msg, "OK");
            }
            else if (action == Loc.T("Prog.DetectOpen"))
            {
                ProgramEnvironmentChecker.OpenInstallPage(result);
            }
        }
        finally
        {
            DetectBtn.IsEnabled = true;
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tcs.TrySetResult(false);
    }
}
