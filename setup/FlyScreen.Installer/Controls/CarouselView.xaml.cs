using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FlyScreen.Installer.Controls;

public partial class CarouselView : UserControl
{
    private readonly List<string> _images = [];
    private int _index;
    private readonly DispatcherTimer _timer;

    public CarouselView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _timer.Tick += (_, _) => Next();
        Loaded += (_, _) =>
        {
            ApplyRoundedClip();
            if (_images.Count > 1) _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyRoundedClip();

    private void ApplyRoundedClip()
    {
        if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0) return;
        Root.Clip = new RectangleGeometry(
            new Rect(0, 0, Root.ActualWidth, Root.ActualHeight),
            Root.CornerRadius.TopLeft,
            Root.CornerRadius.TopLeft);
    }

    public void SetImages(IEnumerable<string> paths)
    {
        _images.Clear();
        foreach (var p in paths)
        {
            if (File.Exists(p)) _images.Add(p);
        }

        DotsPanel.Children.Clear();
        for (var i = 0; i < Math.Max(_images.Count, 1); i++)
        {
            var idx = i;
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                Tag = i
            };
            if (_images.Count > 0)
                dot.MouseLeftButtonUp += (_, _) => Show(idx);
            DotsPanel.Children.Add(dot);
        }

        // 保证原型里始终有 3 个点的视觉（图不够时补灰点）
        while (DotsPanel.Children.Count < 3 && _images.Count == 0)
        {
            DotsPanel.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            });
        }

        if (_images.Count == 0)
        {
            SlideImage.Source = null;
            FallbackBanner.Visibility = Visibility.Visible;
            SetActiveDot(0);
            _timer.Stop();
            return;
        }

        FallbackBanner.Visibility = Visibility.Collapsed;
        Show(0);
        if (_images.Count > 1) _timer.Start();
        else _timer.Stop();
    }

    private void Next()
    {
        if (_images.Count == 0) return;
        Show((_index + 1) % _images.Count);
    }

    private void Show(int index)
    {
        _index = index;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(_images[index], UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            SlideImage.Source = bmp;
            FallbackBanner.Visibility = Visibility.Collapsed;
        }
        catch
        {
            SlideImage.Source = null;
            FallbackBanner.Visibility = Visibility.Visible;
        }

        SetActiveDot(index);
    }

    private void SetActiveDot(int index)
    {
        for (var i = 0; i < DotsPanel.Children.Count; i++)
        {
            if (DotsPanel.Children[i] is not Ellipse d) continue;
            d.Fill = i == index
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        }
    }
}
