using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FlyScreen.DesktopUi.Services;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.DesktopUi;

public partial class HelpWindow : Window
{
    private readonly IDesktopShellHost _host;
    private readonly Action? _afterDiagnostics;
    private readonly GitHubReleaseChecker _updater = new();
    private ReleaseCheckResult? _lastCheck;

    /// <summary>满足 AVLN3001（XAML 运行时加载器/预览器）；正式启动请用带 host 的构造函数。</summary>
    public HelpWindow()
    {
        if (!Design.IsDesignMode)
            throw new InvalidOperationException("HelpWindow requires IDesktopShellHost.");
        _host = null!;
        InitializeComponent();
    }

    public HelpWindow(IDesktopShellHost host, Action? afterDiagnostics = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _afterDiagnostics = afterDiagnostics;
        InitializeComponent();
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
        BuildHelpContent();
    }

    private void BuildHelpContent()
    {
        HelpContent.Children.Clear();
        HelpContent.Children.Add(DocTitle(Loc.T("Help.Doc.Title")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.Intro")));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Quick")));
        AddNumbered(
            Loc.T("Help.Doc.Q1"),
            Loc.T("Help.Doc.Q2"),
            Loc.T("Help.Doc.Q3"),
            Loc.T("Help.Doc.Q4"),
            Loc.T("Help.Doc.Q5"));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Clip")));
        AddBullets(
            Loc.T("Help.Doc.C1"),
            Loc.T("Help.Doc.C2"),
            Loc.T("Help.Doc.C3"),
            Loc.T("Help.Doc.C4"));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Opts")));
        AddBullets(
            Loc.T("Help.Doc.O1"),
            Loc.T("Help.Doc.O2"),
            Loc.T("Help.Doc.O3"),
            Loc.T("Help.Doc.O4"),
            Loc.T("Help.Doc.O5"));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Hot")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.H1")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.H2")));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Net")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.N1")));
        HelpContent.Children.Add(Para(ConfigDirLine()));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Update")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.U1")));
        HelpContent.Children.Add(Para(GitHubReleaseChecker.ReleasesUrl));

        HelpContent.Children.Add(Heading(Loc.T("Help.Doc.Diag")));
        HelpContent.Children.Add(Para(Loc.T("Help.Doc.D1")));
    }

    private static string ConfigDirLine()
    {
        if (OperatingSystem.IsWindows())
            return Loc.T("Help.Doc.N2");
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyScreen");
        return $"配置目录：{dir}";
    }

    private IBrush? TextBrush()
        => TryGetResourceBrush("Text") ?? Brushes.Black;

    private IBrush? MutedBrush()
        => TryGetResourceBrush("Muted") ?? Brushes.Gray;

    private IBrush? TryGetResourceBrush(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b)
            return b;
        if (Resources.TryGetResource(key, null, out var local) && local is IBrush lb)
            return lb;
        return null;
    }

    private TextBlock DocTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        Foreground = TextBrush(),
        Margin = new Thickness(0, 0, 0, 10),
        TextWrapping = TextWrapping.Wrap
    };

    private TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        Foreground = TextBrush(),
        Margin = new Thickness(0, 12, 0, 6),
        TextWrapping = TextWrapping.Wrap
    };

    private TextBlock Para(string text) => new()
    {
        Text = text,
        Foreground = MutedBrush(),
        Margin = new Thickness(0, 0, 0, 4),
        TextWrapping = TextWrapping.Wrap
    };

    private void AddNumbered(params string[] items)
    {
        for (var i = 0; i < items.Length; i++)
            HelpContent.Children.Add(Para($"{i + 1}. {items[i]}"));
    }

    private void AddBullets(params string[] items)
    {
        foreach (var item in items)
            HelpContent.Children.Add(Para("• " + item));
    }

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        Cursor = busy ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait) : Avalonia.Input.Cursor.Default;
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
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

    private async void ApplyUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastCheck is not { Success: true, UpdateAvailable: true })
        {
            ActionStatus.Text = Loc.T("Help.NeedCheckFirst");
            return;
        }

        // 桌面端（Mac/Linux）暂不自动替换二进制：打开 Releases，与 Win「自动更新」同一信息源
        var url = string.IsNullOrWhiteSpace(_lastCheck.ReleaseUrl)
            ? GitHubReleaseChecker.ReleasesUrl
            : _lastCheck.ReleaseUrl;
        ActionStatus.Text =
            $"请下载安装 { _lastCheck.RemoteVersion }" +
            (string.IsNullOrWhiteSpace(_lastCheck.AssetName) ? "" : $"（{_lastCheck.AssetName}）") +
            "。已打开发布页；自动替换安装请在 Windows 端使用。";
        ShowDiag(ActionStatus.Text + "\n\n" + url);
        TryOpenUrl(url);
        await Task.CompletedTask;
    }

    private async void DiagCheck_Click(object? sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            ActionStatus.Text = Loc.T("Help.CheckingConn");
            var diag = new DesktopConnectionDiagnostics(_host);
            var report = await diag.CheckAsync().ConfigureAwait(true);
            ActionStatus.Text = report.Summary;
            ShowDiag(report.FormatText());
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DiagFix_Click(object? sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var diag = new DesktopConnectionDiagnostics(_host);
            var progress = new Progress<string>(s =>
                Dispatcher.UIThread.Post(() => ActionStatus.Text = s));
            var report = await diag.FixAsync(progress).ConfigureAwait(true);
            ActionStatus.Text = report.Summary;
            ShowDiag(report.FormatText());
            _afterDiagnostics?.Invoke();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowDiag(string text)
    {
        DiagOutput.IsVisible = true;
        DiagOutput.Text = text;
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
