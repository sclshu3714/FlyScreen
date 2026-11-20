using FlyScreen.Ui.Maui;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Programs;

namespace FlyScreen.AppMaui;

public partial class ProgramAddPage : ContentPage
{
    private bool _nsManual;
    private bool _fileManual;
    private readonly TaskCompletionSource<bool> _tcs = new();
    private ProgramEnvCheckResult? _lastFail;

    public string ProgramName => NameBox.Text?.Trim() ?? "";
    private readonly IReadOnlyList<ProgramLanguage> _languages = ProgramLanguageInfo.ForCurrentPlatform;

    public ProgramLanguage SelectedLanguage =>
        LangPicker.SelectedIndex >= 0 && LangPicker.SelectedIndex < _languages.Count
            ? _languages[LangPicker.SelectedIndex]
            : _languages[0];
    public string NamespaceValue => NamespaceBox.Text?.Trim() ?? "";
    public string FileNameValue => FileNameBox.Text?.Trim() ?? "";
    public bool EnableUi => EnableUiCheck.IsChecked
                            && SelectedLanguage == ProgramLanguage.CSharp
                            && ProgramUiCompatibility.DefaultCreateUiKindForLocal() != ProgramUiKind.None;
    public ProgramUiKind SelectedUiKind
        => EnableUi ? ProgramUiCompatibility.DefaultCreateUiKindForLocal() : ProgramUiKind.None;
    public Task<bool> Result => _tcs.Task;

    public ProgramAddPage()
    {
        InitializeComponent();
        Helpers.MauiThemeService.PrepareOverlayPage(this);
        Title = Loc.T("Prog.DlgAddTitle");
        Shell.Title = Title;
        LblName.Text = Loc.T("Prog.DlgName");
        LblLang.Text = Loc.T("Prog.DlgLang");
        LblNs.Text = Loc.T("Prog.DlgNamespace");
        LblFile.Text = Loc.T("Prog.DlgFileName");
        LblEnableUi.Text = Loc.T("Prog.DlgEnableUi");
        SaveBtn.Text = Loc.T("Prog.DlgSave");
        CancelBtn.Text = Loc.T("Prog.DlgCancel");
        DetectBtn.Text = Loc.T("Prog.DlgDetect");
        DetectAutoBtn.Text = Loc.T("Prog.DetectAuto");
        DetectOpenBtn.Text = Loc.T("Prog.DetectOpen");

        LangPicker.ItemsSource = _languages
            .Select(ProgramLanguageInfo.DisplayName)
            .ToList();
        LangPicker.SelectedIndex = 0;
        NamespaceBox.Focused += (_, _) => _nsManual = true;
        FileNameBox.Focused += (_, _) => _fileManual = true;
        NamespaceBox.Text = ChinesePinyin.ToNamespace("");
        FileNameBox.Text = ProgramLanguageInfo.DefaultFileName(_languages[0]);
        RefreshExternalDevHint();
        RefreshUiOptions();
    }

    private void RefreshExternalDevHint()
    {
        ExternalDevHint.Text = SelectedLanguage == ProgramLanguage.CSharp
            ? Loc.T("Prog.DlgExternalDevHintCs")
            : Loc.T("Prog.DlgExternalDevHint");
    }

    private void EnableUi_Changed(object? sender, CheckedChangedEventArgs e) => RefreshUiKindHint();

    private void RefreshUiOptions()
    {
        var csharp = SelectedLanguage == ProgramLanguage.CSharp;
        var kind = ProgramUiCompatibility.DefaultCreateUiKindForLocal();
        UiOptionsPanel.IsVisible = csharp;
        if (!csharp)
            EnableUiCheck.IsChecked = false;
        EnableUiCheck.IsEnabled = kind != ProgramUiKind.None;
        RefreshUiKindHint();
    }

    private void RefreshUiKindHint()
    {
        var kind = ProgramUiCompatibility.DefaultCreateUiKindForLocal();
        if (kind == ProgramUiKind.None)
        {
            UiKindHint.Text = Loc.T("Prog.DlgUiKindHintNone");
            return;
        }
        if (!EnableUiCheck.IsChecked)
        {
            UiKindHint.Text = "";
            return;
        }
        UiKindHint.Text = Loc.T(
            "Prog.DlgUiKindHint",
            ProgramUiCompatibility.DisplayName(kind),
            ProgramUiCompatibility.ShareHint(kind));
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
        RefreshExternalDevHint();
        RefreshUiOptions();
        ClearDetectUi();
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
        DetectActions.IsVisible = false;
        _lastFail = null;
        SetDetectStatus(Loc.T("Prog.DetectChecking"), "#6B7280");
        try
        {
            var result = await Task.Run(() => ProgramEnvironmentChecker.Check(lang));
            if (result.Ok)
            {
                SetDetectStatus($"{Loc.T("Prog.DetectOk")} — {result.Detail}", "#059669");
                return;
            }

            _lastFail = result;
            SetDetectStatus($"{Loc.T("Prog.DetectFail")} — {result.Detail}", "#D97706");
            DetectActions.IsVisible = true;
        }
        finally
        {
            DetectBtn.IsEnabled = true;
        }
    }

    private async void DetectAuto_Clicked(object? sender, EventArgs e)
    {
        if (_lastFail is null) return;
        var lang = SelectedLanguage;
        DetectAutoBtn.IsEnabled = DetectOpenBtn.IsEnabled = false;
        SetDetectStatus(Loc.T("Prog.DetectAuto") + "…", "#6B7280");
        try
        {
            var (ok, msg) = await ProgramEnvironmentChecker.TryAutoInstallAsync(lang, _lastFail);
            SetDetectStatus(ok ? $"{Loc.T("Prog.DetectOk")} — {msg}" : $"{Loc.T("Prog.DetectFail")} — {msg}",
                ok ? "#059669" : "#D97706");
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

    private void DetectOpen_Clicked(object? sender, EventArgs e)
    {
        if (_lastFail is null) return;
        var guide = ProgramEnvironmentChecker.BuildManualRepairGuide(SelectedLanguage, _lastFail);
        SetDetectStatus($"{Loc.T("Prog.DetectManualTitle")}\n{guide}", "#D97706");
        if (!string.IsNullOrWhiteSpace(_lastFail.InstallUrl))
            ProgramEnvironmentChecker.OpenInstallPage(_lastFail);
    }

    private void SetDetectStatus(string text, string colorHex)
    {
        DetectStatus.Text = text;
        DetectStatus.TextColor = Color.FromArgb(colorHex);
    }

    private void ClearDetectUi()
    {
        DetectStatus.Text = "";
        DetectActions.IsVisible = false;
        _lastFail = null;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tcs.TrySetResult(false);
    }
}
