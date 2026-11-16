using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.Ui.Maui;

/// <summary>
/// 通用加载遮罩：居中短弧旋转 + 可选文案，可铺满任意等待区域。
/// </summary>
public sealed class LoadingOverlay : ContentView
{
    public static readonly BindableProperty IsActiveProperty = BindableProperty.Create(
        nameof(IsActive), typeof(bool), typeof(LoadingOverlay), false,
        propertyChanged: OnIsActiveChanged);

    public static readonly BindableProperty MessageProperty = BindableProperty.Create(
        nameof(Message), typeof(string), typeof(LoadingOverlay), null,
        propertyChanged: OnMessageChanged);

    public static readonly BindableProperty SpinnerSizeProperty = BindableProperty.Create(
        nameof(SpinnerSize), typeof(double), typeof(LoadingOverlay), 40d,
        propertyChanged: OnSpinnerSizeChanged);

    public static readonly BindableProperty ShowScrimProperty = BindableProperty.Create(
        nameof(ShowScrim), typeof(bool), typeof(LoadingOverlay), true,
        propertyChanged: OnShowScrimChanged);

    private static readonly Color DefaultSpinnerColor = Color.FromArgb("#60A5FA");
    private static readonly Color DefaultMessageColor = Color.FromArgb("#E2E8F0");
    private static readonly Color DefaultScrimColor = Color.FromArgb("#66000000");

    private readonly Ellipse _spinner;
    private readonly Label _message;
    private readonly Border _root;
    private readonly IDispatcherTimer _timer;
    private double _angle;
    private const double StrokeThickness = 3.5;

    public LoadingOverlay()
    {
        InputTransparent = true;
        IsVisible = false;
        HorizontalOptions = LayoutOptions.Fill;
        VerticalOptions = LayoutOptions.Fill;

        _spinner = new Ellipse
        {
            WidthRequest = SpinnerSize,
            HeightRequest = SpinnerSize,
            Stroke = DefaultSpinnerColor,
            StrokeThickness = StrokeThickness,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Rotation = 0
        };
        ApplySpinnerDash();

        _message = new Label
        {
            TextColor = DefaultMessageColor,
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center,
            HorizontalOptions = LayoutOptions.Center,
            IsVisible = false
        };

        _root = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = DefaultScrimColor,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Content = new Grid
            {
                Children =
                {
                    new VerticalStackLayout
                    {
                        Spacing = 14,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                        Children = { _spinner, _message }
                    }
                }
            }
        };
        Content = _root;

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 8) % 360;
            _spinner.Rotation = _angle;
        };
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

    private static void OnIsActiveChanged(BindableObject b, object o, object n)
    {
        if (b is LoadingOverlay overlay)
            overlay.ApplyActive((bool)n);
    }

    private static void OnMessageChanged(BindableObject b, object o, object n)
    {
        if (b is LoadingOverlay overlay)
            overlay.ApplyMessage(n as string);
    }

    private static void OnSpinnerSizeChanged(BindableObject b, object o, object n)
    {
        if (b is not LoadingOverlay overlay) return;
        var size = Math.Max(12, (double)n);
        overlay._spinner.WidthRequest = size;
        overlay._spinner.HeightRequest = size;
        overlay.ApplySpinnerDash();
    }

    private static void OnShowScrimChanged(BindableObject b, object o, object n)
    {
        if (b is LoadingOverlay overlay)
            overlay._root.BackgroundColor = (bool)n ? DefaultScrimColor : Colors.Transparent;
    }

    private void ApplyActive(bool active)
    {
        IsVisible = active;
        if (active)
            _timer.Start();
        else
        {
            _timer.Stop();
            _angle = 0;
            _spinner.Rotation = 0;
        }
    }

    private void ApplyMessage(string? text)
    {
        var has = !string.IsNullOrWhiteSpace(text);
        _message.Text = has ? text! : string.Empty;
        _message.IsVisible = has;
    }

    private void ApplySpinnerDash()
    {
        var size = Math.Max(12, SpinnerSize);
        var circ = Math.PI * Math.Max(1, size - StrokeThickness);
        var dash = Math.Max(8, circ * 0.22);
        var gap = Math.Max(16, circ - dash);
        _spinner.StrokeDashArray = new DoubleCollection { dash, gap };
    }
}
