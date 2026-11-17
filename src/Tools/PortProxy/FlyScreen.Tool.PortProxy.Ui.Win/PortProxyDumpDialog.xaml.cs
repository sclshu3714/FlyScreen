using System.Windows;
using FlyScreen.Helpers.Localization;
using FlyScreen.Ui.Theme.Wpf;

namespace FlyScreen.Tool.PortProxy.Ui.Win;

public partial class PortProxyDumpDialog : Window
{
    public PortProxyDumpDialog(string dumpText, FrameworkElement? themeHost = null)
    {
        InitializeComponent();
        WpfDialogTheme.ApplyTo(this);
        Loaded += (_, _) => WpfDialogTheme.ApplyTo(this);
        Title = Loc.T("Tool.PortProxy.DialogDump");
        CopyBtn.Content = Loc.T("Tool.PortProxy.CopyDump");
        CloseBtn.Content = Loc.T("Tool.PortProxy.Close");
        DumpBox.Text = dumpText ?? "";
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
