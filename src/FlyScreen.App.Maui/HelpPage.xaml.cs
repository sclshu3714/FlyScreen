using FlyScreen.AppMaui.Services;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.AppMaui;

public partial class HelpPage : ContentPage
{
    private readonly MauiAppHost _host;
    private readonly MauiUpdateChecker _updater = new();
    private MauiUpdateCheckResult? _lastCheck;

    public HelpPage(MauiAppHost host)
    {
        InitializeComponent();
        _host = host;
        Title = Loc.T("Help.Title");
        SectionUpdateText.Text = Loc.T("Help.SectionUpdate");
        ActionStatus.Text = Loc.T("Help.SectionHint");
        CheckUpdateBtn.Text = Loc.T("Help.CheckUpdate");
        ApplyUpdateBtn.Text = Loc.T("Help.ApplyUpdate");
        DiagCheckBtn.Text = Loc.T("Help.DiagCheck");
        DiagFixBtn.Text = Loc.T("Help.DiagFix");
        CloseBtn.Text = Loc.T("Help.Close");
        BuildHelpContent();
    }

    private void BuildHelpContent()
    {
        HelpContent.Children.Clear();
        HelpContent.Children.Add(DocTitle(Loc.T("Help.Doc.Title")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.Intro")));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Quick")));
        AddNumbered(Loc.T("Help.Doc.Q1"), Loc.T("Help.Doc.Q2"), Loc.T("Help.Doc.Q3"),
            Loc.T("Help.Doc.Q4"), Loc.T("Help.Doc.Q5"));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Clip")));
        AddBullets(Loc.T("Help.Doc.C1"), Loc.T("Help.Doc.C2"), Loc.T("Help.Doc.C3"), Loc.T("Help.Doc.C4"));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Opts")));
        AddBullets(Loc.T("Help.Doc.O1"), Loc.T("Help.Doc.O2"), Loc.T("Help.Doc.O3"),
            Loc.T("Help.Doc.O4"), Loc.T("Help.Doc.O5"));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Hot")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.H1")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.H2")));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Net")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.N1")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.N2")));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Update")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.U1")));
        HelpContent.Children.Add(Para(MauiUpdateChecker.ReleasesUrl));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Diag")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.D1")));
    }

    private static Label DocTitle(string t) => new()
    {
        Text = t,
        FontSize = 18,
        FontAttributes = FontAttributes.Bold,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static Label Heading(string t) => new()
    {
        Text = t,
        FontSize = 15,
        FontAttributes = FontAttributes.Bold,
        Margin = new Thickness(0, 12, 0, 4)
    };

    private static Label Para(string t) => new()
    {
        Text = t,
        FontSize = 13,
        TextColor = (Color)Application.Current!.Resources["Muted"],
        LineBreakMode = LineBreakMode.WordWrap
    };

    private void AddNumbered(params string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
            HelpContent.Children.Add(Para($"{i + 1}. {lines[i]}"));
    }

    private void AddBullets(params string[] lines)
    {
        foreach (var line in lines)
            HelpContent.Children.Add(Para("· " + line));
    }

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        CheckUpdateBtn.IsEnabled = !busy;
        DiagCheckBtn.IsEnabled = !busy;
        DiagFixBtn.IsEnabled = !busy;
        if (!busy)
            ApplyUpdateBtn.IsEnabled = _lastCheck is { Success: true, UpdateAvailable: true };
    }

    private async void CheckUpdate_Clicked(object? sender, EventArgs e)
    {
        SetBusy(true);
        try
        {
            ActionStatus.Text = Loc.T("Help.CheckingUpdate");
            var result = await _updater.CheckAsync().ConfigureAwait(true);
            _lastCheck = result;
            ApplyUpdateBtn.IsEnabled = result is { Success: true, UpdateAvailable: true };
            ActionStatus.Text = result.Message;
            if (result.Success && result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.ReleaseNotes))
                ShowDiag($"版本 {result.RemoteVersion}\n\n{result.ReleaseNotes}");
            else if (!result.Success)
                ShowDiag(result.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ApplyUpdate_Clicked(object? sender, EventArgs e)
    {
        if (_lastCheck is not { Success: true, UpdateAvailable: true })
        {
            ActionStatus.Text = Loc.T("Help.NeedCheckFirst");
            return;
        }

        var ok = await DisplayAlertAsync(
            Loc.T("Help.ConfirmUpdateTitle"),
            string.Format(Loc.T("Help.ConfirmUpdate"),
                _lastCheck.RemoteVersion,
                _lastCheck.AssetName ?? Loc.T("Help.AssetUnnamed")),
            "是", "否");
        if (!ok) return;

        UpdateProgressBar.IsVisible = true;
        UpdateProgressBar.Progress = 0.3;
        ActionStatus.Text = "正在打开更新页面…";
        try
        {
            var url = string.IsNullOrWhiteSpace(_lastCheck.ReleaseUrl)
                ? MauiUpdateChecker.ReleasesUrl
                : _lastCheck.ReleaseUrl;
            await Launcher.Default.OpenAsync(url);
            ActionStatus.Text = "已打开 Releases 页面，请下载并安装新版本。";
            ShowDiag(ActionStatus.Text + "\n" + url);
        }
        catch (Exception ex)
        {
            ActionStatus.Text = ex.Message;
            ShowDiag(ex.Message);
        }
        finally
        {
            UpdateProgressBar.IsVisible = false;
            UpdateProgressBar.Progress = 0;
        }
    }

    private async void DiagCheck_Clicked(object? sender, EventArgs e)
    {
        SetBusy(true);
        try
        {
            ActionStatus.Text = Loc.T("Help.CheckingConn");
            var diag = new MauiConnectionDiagnostics(_host);
            var report = await diag.CheckAsync().ConfigureAwait(true);
            ActionStatus.Text = report.Summary;
            ShowDiag(report.FormatText());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DiagFix_Clicked(object? sender, EventArgs e)
    {
        SetBusy(true);
        try
        {
            var diag = new MauiConnectionDiagnostics(_host);
            var progress = new Progress<string>(s => MainThread.BeginInvokeOnMainThread(() => ActionStatus.Text = s));
            var report = await diag.FixAsync(progress).ConfigureAwait(true);
            ActionStatus.Text = report.Summary;
            ShowDiag(report.FormatText());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowDiag(string text)
    {
        DiagOutput.Text = text;
        DiagOutput.IsVisible = true;
    }

    private async void Close_Clicked(object? sender, EventArgs e)
        => await Navigation.PopModalAsync();
}
