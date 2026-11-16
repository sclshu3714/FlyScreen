using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace FlyScreen.Ui.Avalonia;

/// <summary>
/// 通用加载遮罩：居中短弧旋转 + 可选文案，可铺满任意等待区域。
/// </summary>
public sealed class LoadingOverlay : Grid
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LoadingOverlay, bool>(nameof(IsActive));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<LoadingOverlay, string?>(nameof(Message));

    public static readonly StyledProperty<double> SpinnerSizeProperty =
        AvaloniaProperty.Register<LoadingOverlay, double>(nameof(SpinnerSize), 40);

    public static readonly StyledProperty<bool> ShowScrimProperty =
        AvaloniaProperty.Register<LoadingOverlay, bool>(nameof(ShowScrim), true);

    private static readonly IBrush DefaultSpinnerBrush =
        new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly IBrush DefaultMessageBrush =
        new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
    private static readonly IBrush DefaultScrimBrush =
        new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));

    private readonly Ellipse _spinner;
    private readonly RotateTransform _rotate = new();
    private readonly TextBlock _message;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private const double StrokeThickness = 3.5;

    public LoadingOverlay()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        Background = DefaultScrimBrush;

        _spinner = new Ellipse
        {
            Width = SpinnerSize,
            Height = SpinnerSize,
            Stroke = DefaultSpinnerBrush,
            StrokeThickness = StrokeThickness,
            StrokeLineCap = PenLineCap.Round,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
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
            IsVisible = false
        };

        Children.Add(new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _spinner, _message }
        });

        _timer.Tick += (_, _) => _rotate.Angle = (_rotate.Angle + 8) % 360;
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public double SpinnerSize
    {
        get => GetValue(SpinnerSizeProperty);
        set => SetValue(SpinnerSizeProperty, value);
    }

    public bool ShowScrim
    {
        get => GetValue(ShowScrimProperty);
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
            ApplyActive(change.GetNewValue<bool>());
        else if (change.Property == MessageProperty)
            ApplyMessage(change.GetNewValue<string?>());
        else if (change.Property == SpinnerSizeProperty)
        {
            var size = Math.Max(12, change.GetNewValue<double>());
            _spinner.Width = size;
            _spinner.Height = size;
            ApplySpinnerDash();
        }
        else if (change.Property == ShowScrimProperty)
            Background = change.GetNewValue<bool>() ? DefaultScrimBrush : Brushes.Transparent;
    }

    private void ApplyActive(bool active)
    {
        IsVisible = active;
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
        _message.IsVisible = has;
    }

    /// <summary>Avalonia Dash 为 DIP；按周长算短弧。</summary>
    private void ApplySpinnerDash()
    {
        var size = Math.Max(12, SpinnerSize);
        var circ = Math.PI * Math.Max(1, size - StrokeThickness);
        var dash = Math.Max(8, circ * 0.22);
        var gap = Math.Max(16, circ - dash);
        _spinner.StrokeDashArray = [dash, gap];
    }
}
