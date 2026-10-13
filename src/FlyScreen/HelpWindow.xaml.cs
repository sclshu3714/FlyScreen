using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Update;
using Media = System.Windows.Media;

namespace FlyScreen;

public partial class HelpWindow : Window
{
    private readonly AppHost _host;
    private readonly GitHubUpdateService _updater = new();
    private UpdateCheckResult? _lastCheck;

    public HelpWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        ThemeService.ApplyToChildWindow(this);
        ApplyLocalizedUi();
    }

    private void ApplyLocalizedUi()
    {
        Title = Loc.T("Help.Title");
        SectionUpdateText.Text = Loc.T("Help.SectionUpdate");
        ActionStatus.Text = Loc.T("Help.SectionHint");
        CheckUpdateBtn.Content = Loc.T("Help.CheckUpdate");
        ApplyUpdateBtn.Content = Loc.T("Help.ApplyUpdate");
        DiagCheckBtn.Content = Loc.T("Help.DiagCheck");
        DiagFixBtn.Content = Loc.T("Help.DiagFix");
        CloseBtn.Content = Loc.T("Help.Close");
        HelpRich.Document = BuildHelpDocument();
        HelpRich.Foreground = Brush("Text");
        HelpRich.Background = Media.Brushes.Transparent;
    }

    private FlowDocument BuildHelpDocument()
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new Media.FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            LineHeight = 22,
            Foreground = Brush("Text")
        };

        doc.Blocks.Add(DocTitle(Loc.T("Help.Doc.Title")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.Intro")));

        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Quick")));
        doc.Blocks.Add(Numbered(
            Loc.T("Help.Doc.Q1"),
            Loc.T("Help.Doc.Q2"),
            Loc.T("Help.Doc.Q3"),
            Loc.T("Help.Doc.Q4"),
            Loc.T("Help.Doc.Q5")));

        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Clip")));
        doc.Blocks.Add(Bullets(
            Loc.T("Help.Doc.C1"),
            Loc.T("Help.Doc.C2"),
            Loc.T("Help.Doc.C3"),
            Loc.T("Help.Doc.C4")));

        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Opts")));
        doc.Blocks.Add(Bullets(
            Loc.T("Help.Doc.O1"),
            Loc.T("Help.Doc.O2"),
            Loc.T("Help.Doc.O3"),
            Loc.T("Help.Doc.O4"),
            Loc.T("Help.Doc.O5")));

        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Hot")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.H1")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.H2")));

        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Net")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.N1")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.N2")));

        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Update")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.U1")));
        doc.Blocks.Add(Para(GitHubUpdateService.ReleasesUrl));

        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Diag")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.D1")));

        return doc;
    }

    private Media.Brush Brush(string key)
        => Resources[key] as Media.Brush ?? Media.Brushes.Black;

    private Paragraph DocTitle(string text) => new(new Run(text)
    {
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brush("Text")
    })
    { Margin = new Thickness(0, 0, 0, 10) };

    private Paragraph Heading(string text) => new(new Run(text)
    {
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brush("Text")
    })
    { Margin = new Thickness(0, 12, 0, 6) };

    private Paragraph Para(string text) => new(new Run(text)
    {
        Foreground = Brush("Muted")
    })
    { Margin = new Thickness(0, 0, 0, 4) };

    private List Numbered(params string[] items)
    {
        var list = new List { MarkerStyle = TextMarkerStyle.Decimal, Margin = new Thickness(8, 0, 0, 4) };
        foreach (var item in items)
            list.ListItems.Add(new ListItem(Para(item)));
        return list;
    }

    private List Bullets(params string[] items)
    {
        var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(8, 0, 0, 4) };
        foreach (var item in items)
            list.ListItems.Add(new ListItem(Para(item)));
        return list;
    }

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
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
            {
                ShowDiag($"版本 {result.RemoteVersion}\n\n{result.ReleaseNotes}");
            }
            else if (!result.Success)
            {
                ShowDiag(result.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCheck is not { Success: true, UpdateAvailable: true })
        {
            ActionStatus.Text = Loc.T("Help.NeedCheckFirst");
            return;
        }

        var confirm = MessageBox.Show(
            this,
            string.Format(
                Loc.T("Help.ConfirmUpdate"),
                _lastCheck.RemoteVersion,
                _lastCheck.AssetName ?? Loc.T("Help.AssetUnnamed")),
            Loc.T("Help.ConfirmUpdateTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<double>(p =>
                ActionStatus.Text = string.Format(Loc.T("Help.Downloading"), p));
            var apply = await _updater.DownloadAndApplyAsync(_lastCheck, progress).ConfigureAwait(true);
            ActionStatus.Text = apply.Message;
            ShowDiag(apply.Message);
            if (apply.ShouldExit)
            {
                await Task.Delay(400).ConfigureAwait(true);
                Application.Current.Shutdown();
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DiagCheck_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            ActionStatus.Text = Loc.T("Help.CheckingConn");
            var diag = new ConnectionDiagnostics(_host);
            var report = await diag.CheckAsync().ConfigureAwait(true);
            ActionStatus.Text = report.Summary;
            ShowDiag(report.FormatText());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DiagFix_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var diag = new ConnectionDiagnostics(_host);
            var progress = new Progress<string>(s => ActionStatus.Text = s);
            var report = await diag.FixAsync(progress).ConfigureAwait(true);
            ActionStatus.Text = report.Summary;
            ShowDiag(report.FormatText());

            if (Owner is MainWindow mw)
                mw.ReloadAfterDiagnostics();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowDiag(string text)
    {
        DiagOutput.Visibility = Visibility.Visible;
        DiagOutput.Text = text;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
