using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FlyScreen.Ui.Wpf;

/// <summary>
/// 通用加载遮罩：居中短弧旋转 + 可选文案，可铺满任意等待区域。
/// </summary>
public sealed class LoadingOverlay : Grid
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LoadingOverlay),
        new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(LoadingOverlay),
        new PropertyMetadata(null, OnMessageChanged));

    public static readonly DependencyProperty SpinnerSizeProperty = DependencyProperty.Register(
        nameof(SpinnerSize), typeof(double), typeof(LoadingOverlay),
        new PropertyMetadata(40d, OnSpinnerMetricsChanged));

    public static readonly DependencyProperty ShowScrimProperty = DependencyProperty.Register(
        nameof(ShowScrim), typeof(bool), typeof(LoadingOverlay),
        new PropertyMetadata(true, OnShowScrimChanged));

    private static readonly Brush DefaultSpinnerBrush =
        CreateFrozen(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush DefaultMessageBrush =
        CreateFrozen(Color.FromRgb(0xE2, 0xE8, 0xF0));
    private static readonly Brush DefaultScrimBrush =
        CreateFrozen(Color.FromArgb(0x66, 0, 0, 0));

    private readonly Ellipse _spinner;
    private readonly RotateTransform _rotate = new();
    private readonly TextBlock _message;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private const double StrokeThickness = 3.5;

    public LoadingOverlay()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Background = DefaultScrimBrush;

        _spinner = new Ellipse
        {
            Width = SpinnerSize,
            Height = SpinnerSize,
            Stroke = DefaultSpinnerBrush,
            StrokeThickness = StrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rotate,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplySpinnerDash();

        _message = new TextBlock
        {
            Foreground = DefaultMessageBrush,
            FontSize = 13,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _spinner, _message }
        };
        Children.Add(stack);

        _timer.Tick += (_, _) => _rotate.Angle = (_rotate.Angle + 8) % 360;
        Unloaded += (_, _) => _timer.Stop();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public double SpinnerSize
    {
        get => (double)GetValue(SpinnerSizeProperty);
        set => SetValue(SpinnerSizeProperty, value);
    }

    public bool ShowScrim
    {
        get => (bool)GetValue(ShowScrimProperty);
        set => SetValue(ShowScrimProperty, value);
    }

    /// <summary>显示加载（可附带文案）。</summary>
    public void Show(string? message = null)
    {
        if (message is not null)
            Message = message;
        IsActive = true;
    }

    /// <summary>隐藏并停止动画。</summary>
    public void Hide() => IsActive = false;

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LoadingOverlay overlay)
            overlay.ApplyActive((bool)e.NewValue);
    }

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LoadingOverlay overlay)
            overlay.ApplyMessage(e.NewValue as string);
    }

    private static void OnSpinnerMetricsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not LoadingOverlay overlay) return;
        var size = Math.Max(12, (double)e.NewValue);
        overlay._spinner.Width = size;
        overlay._spinner.Height = size;
        overlay.ApplySpinnerDash();
    }

    private static void OnShowScrimChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LoadingOverlay overlay)
            overlay.Background = (bool)e.NewValue ? DefaultScrimBrush : Brushes.Transparent;
    }

    private void ApplyActive(bool active)
    {
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active)
            _timer.Start();
        else
        {
            _timer.Stop();
            _rotate.Angle = 0;
        }
    }

    private void ApplyMessage(string? text)
    {
        var has = !string.IsNullOrWhiteSpace(text);
        _message.Text = has ? text! : string.Empty;
        _message.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>WPF Dash 单位为 StrokeThickness 倍数，按周长算短弧。</summary>
    private void ApplySpinnerDash()
    {
        var size = Math.Max(12, SpinnerSize);
        var radius = Math.Max(1, (size - StrokeThickness) / 2);
        var units = 2 * Math.PI * radius / StrokeThickness;
        var dash = Math.Max(1.5, units * 0.22);
        var gap = Math.Max(4, units - dash);
        _spinner.StrokeDashArray = new DoubleCollection([dash, gap]);
    }

    private static SolidColorBrush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
