using System.Windows;
using System.Windows.Media;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.Tool.PortProxy.Ui.Win;

public partial class PortProxyDumpDialog : Window
{
    public PortProxyDumpDialog(string dumpText, FrameworkElement? themeHost = null)
    {
        InitializeComponent();
        InheritHostBrushes(themeHost ?? Owner as FrameworkElement);
        Title = Loc.T("Tool.PortProxy.DialogDump");
        CopyBtn.Content = Loc.T("Tool.PortProxy.CopyDump");
        CloseBtn.Content = Loc.T("Tool.PortProxy.Close");
        DumpBox.Text = dumpText ?? "";
    }

    private void InheritHostBrushes(FrameworkElement? host)
    {
        if (host is null) return;
        foreach (var key in new[]
                 {
                     "Blue", "BluePress", "BlueSoft", "BlueHover", "Muted", "Line", "Text",
                     "WinBg", "CardBg", "ListBg", "SubtleBg", "ControlBg", "ControlBorder", "ControlHover"
                 })
        {
            if (host.TryFindResource(key) is SolidColorBrush brush)
                Resources[key] = brush.Clone();
        }
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(DumpBox.Text ?? "");
            CopyBtn.Content = Loc.T("Tool.PortProxy.Copied");
        }
        catch
        {
            /* ignore */
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
