using System.Windows;

namespace FlyScreen;

/// <summary>
/// WPF 本地化字符串壳：避免 XAML 引用 System.String（.NET 10 设计器/加载器均不稳定）。
/// Freezable 才能作为 ResourceDictionary 中 Binding 的可靠 Source。
/// </summary>
public sealed class StringBox : Freezable
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(string),
            typeof(StringBox),
            new PropertyMetadata(""));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value ?? "");
    }

    protected override Freezable CreateInstanceCore() => new StringBox();
}
