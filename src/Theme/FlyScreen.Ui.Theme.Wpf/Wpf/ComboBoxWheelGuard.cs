using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyScreen.Ui.Theme.Wpf;

/// <summary>
/// 下拉收起时屏蔽滚轮改选，并把滚轮交给外层 <see cref="ScrollViewer"/>。
/// </summary>
public static class ComboBoxWheelGuard
{
    public static readonly DependencyProperty SuppressSelectionProperty =
        DependencyProperty.RegisterAttached(
            "SuppressSelection",
            typeof(bool),
            typeof(ComboBoxWheelGuard),
            new PropertyMetadata(false, OnSuppressChanged));

    public static void SetSuppressSelection(DependencyObject element, bool value)
        => element.SetValue(SuppressSelectionProperty, value);

    public static bool GetSuppressSelection(DependencyObject element)
        => (bool)element.GetValue(SuppressSelectionProperty);

    private static void OnSuppressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;
        combo.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue)
            combo.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen)
            return;

        e.Handled = true;

        for (var p = VisualTreeHelper.GetParent(combo); p != null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is not ScrollViewer sv) continue;
            // 与常见列表滚轮手感接近：一次约 3 行
            var steps = Math.Max(1, Math.Abs(e.Delta) / 120);
            for (var i = 0; i < steps * 3; i++)
            {
                if (e.Delta > 0) sv.LineUp();
                else sv.LineDown();
            }
            break;
        }
    }
}
