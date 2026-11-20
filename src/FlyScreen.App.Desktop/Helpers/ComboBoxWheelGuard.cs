using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// 下拉收起时屏蔽滚轮改选，并把滚轮交给外层 <see cref="ScrollViewer"/>。
/// </summary>
public static class ComboBoxWheelGuard
{
    public static readonly AttachedProperty<bool> SuppressSelectionProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, bool>(
            "SuppressSelection", typeof(ComboBoxWheelGuard), defaultValue: false);

    static ComboBoxWheelGuard()
    {
        SuppressSelectionProperty.Changed.AddClassHandler<ComboBox>(OnSuppressChanged);
    }

    public static void SetSuppressSelection(AvaloniaObject element, bool value)
        => element.SetValue(SuppressSelectionProperty, value);

    public static bool GetSuppressSelection(AvaloniaObject element)
        => element.GetValue(SuppressSelectionProperty);

    private static void OnSuppressChanged(ComboBox combo, AvaloniaPropertyChangedEventArgs e)
    {
        combo.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel);
        if (e.GetNewValue<bool>())
            combo.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);
    }

    private static void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen)
            return;

        e.Handled = true;
        var sv = combo.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (sv == null) return;

        const double line = 48;
        var dy = e.Delta.Y * line;
        sv.Offset = sv.Offset.WithY(sv.Offset.Y - dy);
    }
}
