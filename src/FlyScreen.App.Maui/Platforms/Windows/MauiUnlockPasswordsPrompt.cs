using FlyScreen.Helpers.Localization;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>Maui-Win：缺解锁密码时的设备/密码表格（模态 Page）。</summary>
public static class MauiUnlockPasswordsPrompt
{
    public static async Task<IReadOnlyDictionary<string, string>?> PromptAsync(
        IReadOnlyList<(string MachineName, string Label)> machines)
    {
        if (machines.Count == 0)
            return new Dictionary<string, string>();

        var page = new UnlockPasswordsPage(machines);
        var nav = Application.Current?.Windows?.FirstOrDefault()?.Page?.Navigation
                  ?? Application.Current?.MainPage?.Navigation;
        if (nav == null) return null;

        await nav.PushModalAsync(page).ConfigureAwait(true);
        try
        {
            return await page.Completion.ConfigureAwait(true);
        }
        finally
        {
            try { await nav.PopModalAsync().ConfigureAwait(true); }
            catch { /* ignore */ }
        }
    }

    private sealed class UnlockPasswordsPage : ContentPage
    {
        private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _tcs = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string>?> Completion => _tcs.Task;

        public UnlockPasswordsPage(IReadOnlyList<(string MachineName, string Label)> machines)
        {
            Title = Loc.T("Tray.UnlockPasswords.Title");
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) }
                },
                RowDefinitions = { new RowDefinition { Height = GridLength.Auto } },
                Padding = new Thickness(16),
                RowSpacing = 8,
                ColumnSpacing = 8
            };

            grid.Add(new Label
            {
                Text = Loc.T("Tray.UnlockPasswords.Device"),
                FontAttributes = FontAttributes.Bold,
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);
            grid.Add(new Label
            {
                Text = Loc.T("Tray.UnlockPasswords.Password"),
                FontAttributes = FontAttributes.Bold,
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
                    VerticalOptions = LayoutOptions.Center,
                    LineBreakMode = LineBreakMode.TailTruncation
                }, 0, row);

                var entry = new Entry
                {
                    IsPassword = true,
                    Placeholder = Loc.T("Tray.UnlockPasswords.Password"),
                    HeightRequest = 40
                };
                _entries[name] = entry;
                grid.Add(entry, 1, row);
            }

            var ok = new Button
            {
                Text = Loc.T("Tray.UnlockPasswords.Ok"),
                HorizontalOptions = LayoutOptions.End
            };
            var cancel = new Button
            {
                Text = Loc.T("Tray.UnlockPasswords.Cancel"),
                HorizontalOptions = LayoutOptions.End
            };
            ok.Clicked += (_, _) =>
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, entry) in _entries)
                {
                    var pwd = entry.Text ?? "";
                    if (string.IsNullOrEmpty(pwd)) continue;
                    map[name] = pwd;
                }

                if (map.Count == 0) return;
                _tcs.TrySetResult(map);
            };
            cancel.Clicked += (_, _) => _tcs.TrySetResult(null);

            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Spacing = 12,
                    Children =
                    {
                        grid,
                        new HorizontalStackLayout
                        {
                            Spacing = 8,
                            HorizontalOptions = LayoutOptions.End,
                            Children = { ok, cancel }
                        }
                    }
                }
            };
        }
    }
}
