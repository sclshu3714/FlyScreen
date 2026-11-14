using System.Windows;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.Tool.PortProxy.Ui.Win;

public partial class PortProxyDumpDialog : Window
{
    public PortProxyDumpDialog(string dumpText)
    {
        InitializeComponent();
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
