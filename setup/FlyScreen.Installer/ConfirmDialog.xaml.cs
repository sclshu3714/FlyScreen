using System.Windows;
using System.Windows.Media;

namespace FlyScreen.Installer;

public enum ConfirmDialogKind
{
    Question,
    Warning,
    Info,
    Error
}

public partial class ConfirmDialog : Window
{
    public bool? Confirmed { get; private set; }

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static bool Ask(
        Window owner,
        string title,
        string message,
        string yesText = "确定",
        string noText = "取消",
        ConfirmDialogKind kind = ConfirmDialogKind.Question,
        bool defaultYes = true)
    {
        var dlg = new ConfirmDialog
        {
            Owner = owner,
            Title = title
        };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.YesButton.Content = yesText;
        dlg.NoButton.Content = noText;
        dlg.NoButton.Visibility = Visibility.Visible;
        dlg.ApplyKind(kind);
        if (defaultYes) dlg.YesButton.IsDefault = true;
        else dlg.NoButton.IsDefault = true;
        dlg.ShowDialog();
        return dlg.Confirmed == true;
    }

    public static void Alert(
        Window owner,
        string title,
        string message,
        ConfirmDialogKind kind = ConfirmDialogKind.Info,
        string okText = "知道了")
    {
        var dlg = new ConfirmDialog
        {
            Owner = owner,
            Title = title
        };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.YesButton.Content = okText;
        dlg.NoButton.Visibility = Visibility.Collapsed;
        dlg.ApplyKind(kind);
        dlg.ShowDialog();
    }

    private void ApplyKind(ConfirmDialogKind kind)
    {
        switch (kind)
        {
            case ConfirmDialogKind.Warning:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7));
                IconText.Text = "!";
                IconText.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
                break;
            case ConfirmDialogKind.Error:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
                IconText.Text = "!";
                IconText.Foreground = (Brush)FindResource("DangerBrush");
                break;
            case ConfirmDialogKind.Info:
                IconBadge.Background = (Brush)FindResource("AccentSoftBrush");
                IconText.Text = "i";
                IconText.Foreground = (Brush)FindResource("AccentBrush");
                break;
            default:
                IconBadge.Background = (Brush)FindResource("AccentSoftBrush");
                IconText.Text = "?";
                IconText.Foreground = (Brush)FindResource("AccentBrush");
                break;
        }
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
