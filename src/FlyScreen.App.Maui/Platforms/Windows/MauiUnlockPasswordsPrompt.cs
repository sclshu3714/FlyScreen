using FlyScreen.AppMaui.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Ui.Maui;
using FlyScreen.Ui.Theme;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>Maui-Win：独立小窗录入解锁密码（禁止 PushModal 嵌进主窗；无投影按钮 / 无最大最小化）。</summary>
public static class MauiUnlockPasswordsPrompt
{
    private static int _promptOpen;

    public static async Task<IReadOnlyDictionary<string, string>?> PromptAsync(
        IReadOnlyList<(string MachineName, string Label)> machines)
    {
        if (machines.Count == 0)
            return new Dictionary<string, string>();

        if (Interlocked.CompareExchange(ref _promptOpen, 1, 0) != 0)
            return null;

        var app = Application.Current;
        if (app == null)
        {
            Interlocked.Exchange(ref _promptOpen, 0);
            return null;
        }

        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, string>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var title = Loc.T("Tray.UnlockPasswords.Title");
        var page = new UnlockPasswordsPage(machines, result => tcs.TrySetResult(result));
        var height = Math.Clamp(130 + machines.Count * 48, 170, 440);
        var window = new Window(page)
        {
            Title = title,
            Width = 390,
            Height = height,
            MinimumWidth = 360,
            MaximumWidth = 440,
            MinimumHeight = 150
        };

        window.Created += (_, _) => ApplyUnlockWindowChrome(window, title);
        window.HandlerChanged += (_, _) => ApplyUnlockWindowChrome(window, title);

        app.OpenWindow(window);
        _ = RefreshChromeDelayedAsync(window, title);

        try
        {
            return await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _promptOpen, 0);
            try { app.CloseWindow(window); }
            catch { /* ignore */ }
        }
    }

    private static async Task RefreshChromeDelayedAsync(Window window, string title)
    {
#if WINDOWS
        foreach (var ms in new[] { 30, 80, 160, 320, 600 })
        {
            try
            {
                await Task.Delay(ms).ConfigureAwait(true);
                MainThread.BeginInvokeOnMainThread(() => ApplyUnlockWindowChrome(window, title));
            }
            catch
            {
                // ignore
            }
        }
#else
        await Task.CompletedTask.ConfigureAwait(false);
#endif
    }

    private static void ApplyUnlockWindowChrome(Window window, string title)
    {
#if WINDOWS
        try
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                return;

            try { native.Title = title; } catch { /* ignore */ }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            try { appWindow.Title = title; } catch { /* ignore */ }

            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.IsMaximizable = false;
                op.IsMinimizable = false;
                op.IsResizable = false;
            }

            try
            {
                var work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
                var w = appWindow.Size.Width;
                var h = appWindow.Size.Height;
                appWindow.Move(new global::Windows.Graphics.PointInt32(
                    work.X + (work.Width - w) / 2,
                    work.Y + (work.Height - h) / 2));
            }
            catch { /* ignore */ }

            if (native.Content is Microsoft.UI.Xaml.DependencyObject tree)
            {
                if (FindByName(tree, "TitleProjectBtn") is Microsoft.UI.Xaml.Controls.Button projectBtn)
                    projectBtn.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

                if (FindByName(tree, "AppTitle") is Microsoft.UI.Xaml.Controls.TextBlock appTitle)
                    appTitle.Text = title;
            }
        }
        catch
        {
            // ignore
        }
#endif
    }

#if WINDOWS
    private static Microsoft.UI.Xaml.DependencyObject? FindByName(
        Microsoft.UI.Xaml.DependencyObject parent, string name)
    {
        if (parent is Microsoft.UI.Xaml.FrameworkElement fe && fe.Name == name)
            return parent;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var found = FindByName(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i), name);
            if (found != null) return found;
        }
        return null;
    }
#endif

    private sealed class UnlockPasswordsPage : ContentPage
    {
        private readonly Dictionary<string, SecurePasswordBox> _entries = new(StringComparer.OrdinalIgnoreCase);

        public UnlockPasswordsPage(
            IReadOnlyList<(string MachineName, string Label)> machines,
            Action<IReadOnlyDictionary<string, string>?> complete)
        {
            Title = Loc.T("Tray.UnlockPasswords.Title");
            MauiThemeService.ApplyToDialog(this);

            var colors = ThemePalette.ForDialog(MauiThemeService.Current, maui: true);
            var text = Color.FromArgb(colors.Text);
            var muted = Color.FromArgb(colors.Muted);
            var line = Color.FromArgb(colors.ControlBorder);
            var controlBg = Color.FromArgb(colors.ControlBg);

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(132) },
                    new ColumnDefinition { Width = new GridLength(200) }
                },
                RowDefinitions = { new RowDefinition { Height = GridLength.Auto } },
                RowSpacing = 6,
                ColumnSpacing = 8,
                HorizontalOptions = LayoutOptions.Center
            };

            grid.Add(new Label
            {
                Text = Loc.T("Tray.UnlockPasswords.Device"),
                FontAttributes = FontAttributes.Bold,
                TextColor = muted,
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);
            grid.Add(new Label
            {
                Text = Loc.T("Tray.UnlockPasswords.Password"),
                FontAttributes = FontAttributes.Bold,
                TextColor = muted,
                VerticalOptions = LayoutOptions.Center
            }, 1, 0);

            for (var i = 0; i < machines.Count; i++)
            {
                var (name, label) = machines[i];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = i + 1;
                grid.Add(new Label
                {
                    Text = label,
                    TextColor = text,
                    VerticalOptions = LayoutOptions.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 1
                }, 0, row);

                var entry = new SecurePasswordBox
                {
                    Placeholder = Loc.T("Tray.UnlockPasswords.Password"),
                    ShowPasswordTip = Loc.T("Sec.ShowPassword"),
                    HidePasswordTip = Loc.T("Sec.HidePassword"),
                    HeightRequest = 40,
                    WidthRequest = 200
                };
                entry.ApplyTheme(text, muted, line, controlBg);
                _entries[name] = entry;
                grid.Add(entry, 1, row);
            }

            var ok = new Button
            {
                Text = Loc.T("Tray.UnlockPasswords.Ok"),
                WidthRequest = 88,
                HeightRequest = 36
            };
            var cancel = new Button
            {
                Text = Loc.T("Tray.UnlockPasswords.Cancel"),
                WidthRequest = 88,
                HeightRequest = 36
            };
            if (Application.Current?.Resources.TryGetValue("DlgPrimaryBtn", out var primary) == true
                && primary is Style primaryStyle)
                ok.Style = primaryStyle;
            if (Application.Current?.Resources.TryGetValue("DlgNormalBtn", out var normal) == true
                && normal is Style normalStyle)
                cancel.Style = normalStyle;

            ok.Clicked += (_, _) =>
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, entry) in _entries)
                {
                    var pwd = entry.Password ?? "";
                    if (string.IsNullOrEmpty(pwd)) continue;
                    map[name] = pwd;
                }

                if (map.Count == 0) return;
                complete(map);
            };
            cancel.Clicked += (_, _) => complete(null);

            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16),
                Spacing = 12,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    grid,
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        HorizontalOptions = LayoutOptions.Center,
                        Children = { ok, cancel }
                    }
                }
            };

            Appearing += (_, _) =>
            {
                MauiThemeService.ApplyToDialog(this);
                foreach (var entry in _entries.Values)
                    entry.ApplyTheme(text, muted, line, controlBg);
            };
        }
    }
}
