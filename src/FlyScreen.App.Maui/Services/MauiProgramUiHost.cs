using FlyScreen.Services.Programs;

namespace FlyScreen.AppMaui.Services;

/// <summary>进程内展示子程序 MAUI 页（简易内置页；程序目录 XAML 供编辑/共享）。</summary>
public sealed class MauiProgramUiHost : IProgramUiHost
{
    public Task<bool?> ShowAsync(string uiDocumentName, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                var nav = ResolveNavigation();
                if (nav == null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var page = BuildPage(() =>
                {
                    _ = CloseAsync(nav, tcs, true);
                });
                await nav.PushModalAsync(page).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static ContentPage BuildPage(Action onClose)
    {
        var close = new Button
        {
            Text = "关闭",
            WidthRequest = 120,
            HorizontalOptions = LayoutOptions.Center
        };
        close.Clicked += (_, _) => onClose();
        return new ContentPage
        {
            Title = "FlyScreen Program",
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 16,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label
                    {
                        Text = "Hello from MAUI UI",
                        FontSize = 20,
                        HorizontalOptions = LayoutOptions.Center
                    },
                    close
                }
            }
        };
    }

    private static async Task CloseAsync(INavigation nav, TaskCompletionSource<bool?> tcs, bool ok)
    {
        try
        {
            if (nav.ModalStack.Count > 0)
                await nav.PopModalAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }
        tcs.TrySetResult(ok);
    }

    private static INavigation? ResolveNavigation()
    {
        try
        {
            var window = Application.Current?.Windows?.FirstOrDefault();
            var page = window?.Page;
            return page?.Navigation ?? (page as NavigationPage)?.Navigation;
        }
        catch
        {
            return null;
        }
    }
}
