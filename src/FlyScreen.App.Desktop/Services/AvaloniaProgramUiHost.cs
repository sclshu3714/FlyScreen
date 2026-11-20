using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi.Services;

/// <summary>Avalonia 桌面：弹出简易子程序窗口。</summary>
public sealed class AvaloniaProgramUiHost : IProgramUiHost
{
    public Task<bool?> ShowAsync(string uiDocumentName, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

                Window? win = null;
                win = new Window
                {
                    Title = "FlyScreen Program",
                    Width = 520,
                    Height = 360,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = BuildContent(() =>
                    {
                        try { win?.Close(); } catch { /* ignore */ }
                    })
                };
                win.Closed += (_, _) => tcs.TrySetResult(true);

                if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow != null)
                {
                    _ = win.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    win.Show();
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static Control BuildContent(Action onClose)
    {
        var btn = new Button { Content = "关闭", Width = 120, Height = 32 };
        btn.Click += (_, _) => onClose();
        return new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Hello from Avalonia UI", FontSize = 20 },
                btn
            }
        };
    }
}
