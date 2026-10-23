using FlyScreen.Models;

namespace FlyScreen.AppMaui;

/// <summary>传输列表行：列宽与表头共享；窄屏可隐藏对端/大小，文件名优先。</summary>
public sealed class TransferListRowView : ContentView
{
    public static double[]? SharedColWidths;

    private readonly Grid _grid;
    private readonly View _peer;
    private readonly View _size;

    public TransferListRowView()
    {
        var dir = new Label { FontSize = 12, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        dir.SetBinding(Label.TextProperty, nameof(TransferItem.DirectionText));

        var name = new Label
        {
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            TextDecorations = TextDecorations.Underline
        };
        name.SetDynamicResource(Label.TextColorProperty, "Primary");
        name.SetBinding(Label.TextProperty, nameof(TransferItem.FileName));
        name.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                if (BindingContext is TransferItem item && FindMainPage() is { } page)
                    page.RevealTransferPath(item);
            })
        });

        var peer = new Label { FontSize = 12, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        peer.SetBinding(Label.TextProperty, nameof(TransferItem.PeerName));
        _peer = peer;

        var size = new Label { FontSize = 12, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        size.SetBinding(Label.TextProperty, nameof(TransferItem.SizeText));
        _size = size;

        var status = new Label { FontSize = 12, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        status.SetBinding(Label.TextProperty, nameof(TransferItem.Status));

        var deleteBtn = new Button
        {
            Text = "🗑",
            FontSize = 14,
            TextColor = Color.FromArgb("#EF4444"),
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            WidthRequest = 28,
            HeightRequest = 28,
            Padding = 0,
            VerticalOptions = LayoutOptions.Center
        };
        deleteBtn.Clicked += async (_, _) =>
        {
            if (BindingContext is TransferItem item && FindMainPage() is { } page)
                await page.DeleteTransferAsync(item);
        };

        _grid = new Grid
        {
            Padding = new Thickness(10, 8),
            ColumnSpacing = 4,
            MinimumHeightRequest = 40
        };
        _grid.Add(dir, 0);
        _grid.Add(name, 1);
        _grid.Add(peer, 2);
        _grid.Add(size, 3);
        _grid.Add(status, 4);
        _grid.Add(deleteBtn, 5);

        var progress = new ProgressBar
        {
            HeightRequest = 40,
            VerticalOptions = LayoutOptions.Fill,
            BackgroundColor = Colors.Transparent,
            ProgressColor = Color.FromArgb("#402563EB"),
            InputTransparent = true
        };
        progress.SetBinding(ProgressBar.ProgressProperty, nameof(TransferItem.ProgressFraction));
        progress.SetBinding(IsVisibleProperty, nameof(TransferItem.ShowProgressBackground));

        var root = new Grid();
        root.Add(progress);
        root.Add(_grid);
        Content = root;
        Loaded += (_, _) => ApplySharedWidths();
    }

    public void ApplySharedWidths()
    {
        if (SharedColWidths is not { Length: 6 }) return;
        _grid.ColumnDefinitions = new ColumnDefinitionCollection
        {
            new(new GridLength(SharedColWidths[0])),
            new(new GridLength(SharedColWidths[1])),
            new(new GridLength(SharedColWidths[2])),
            new(new GridLength(SharedColWidths[3])),
            new(new GridLength(SharedColWidths[4])),
            new(new GridLength(SharedColWidths[5])),
        };
        _peer.IsVisible = SharedColWidths[2] > 1;
        _size.IsVisible = SharedColWidths[3] > 1;
    }

    private MainPage? FindMainPage()
    {
        Element? e = this;
        while (e != null)
        {
            if (e is MainPage p) return p;
            e = e.Parent;
        }
        return null;
    }
}
