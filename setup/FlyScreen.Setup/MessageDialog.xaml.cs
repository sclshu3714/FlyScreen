using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyScreen.Setup;

public enum MessageDialogKind
{
    Info,
    Warning,
    Error,
    Question,
    Success
}

public partial class MessageDialog : Window
{
    private string? _folderPath;
    public bool? Confirmed { get; private set; }

    public MessageDialog()
    {
        InitializeComponent();
    }

    public static void Alert(
        Window owner,
        string title,
        string message,
        MessageDialogKind kind = MessageDialogKind.Info,
        string? folderPath = null,
        string okText = "确定")
    {
        var dlg = Create(owner, title, message, kind, folderPath);
        dlg.YesButton.Content = okText;
        dlg.NoButton.Visibility = Visibility.Collapsed;
        dlg.ShowDialog();
    }

    public static bool Ask(
        Window owner,
        string title,
        string message,
        string yesText = "确定",
        string noText = "取消",
        MessageDialogKind kind = MessageDialogKind.Question,
        string? folderPath = null,
        bool defaultYes = true)
    {
        var dlg = Create(owner, title, message, kind, folderPath);
        dlg.YesButton.Content = yesText;
        dlg.NoButton.Content = noText;
        dlg.NoButton.Visibility = Visibility.Visible;
        if (defaultYes) dlg.YesButton.IsDefault = true;
        else dlg.NoButton.IsDefault = true;
        dlg.ShowDialog();
        return dlg.Confirmed == true;
    }

    /// <summary>打包完成等场景：确定 + 打开文件夹。</summary>
    public static void CompleteWithFolder(Window owner, string title, string message, string folderPath)
    {
        Alert(owner, title, message, MessageDialogKind.Success, folderPath, "确定");
    }

    private static MessageDialog Create(
        Window owner,
        string title,
        string message,
        MessageDialogKind kind,
        string? folderPath)
    {
        var dlg = new MessageDialog
        {
            Owner = owner,
            Title = title
        };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.ApplyKind(kind);
        dlg.SetFolder(folderPath);
        return dlg;
    }

    private void SetFolder(string? folderPath)
    {
        _folderPath = string.IsNullOrWhiteSpace(folderPath) ? null : folderPath.Trim();
        if (_folderPath == null)
        {
            FolderPanel.Visibility = Visibility.Collapsed;
            return;
        }

        FolderPanel.Visibility = Visibility.Visible;
        FolderPathText.Text = _folderPath;
    }

    private void ApplyKind(MessageDialogKind kind)
    {
        switch (kind)
        {
            case MessageDialogKind.Warning:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7));
                IconText.Text = "!";
                IconText.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
                break;
            case MessageDialogKind.Error:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
                IconText.Text = "!";
                IconText.Foreground = (Brush)FindResource("DangerBrush");
                break;
            case MessageDialogKind.Success:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(0xD1, 0xFA, 0xE5));
                IconText.Text = "✓";
                IconText.Foreground = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
                break;
            case MessageDialogKind.Question:
                IconBadge.Background = (Brush)FindResource("AccentSoftBrush");
                IconText.Text = "?";
                IconText.Foreground = (Brush)FindResource("AccentBrush");
                break;
            default:
                IconBadge.Background = (Brush)FindResource("AccentSoftBrush");
                IconText.Text = "i";
                IconText.Foreground = (Brush)FindResource("AccentBrush");
                break;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder();

    private void FolderPath_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenFolder();
    }

    private void OpenFolder()
    {
        if (string.IsNullOrWhiteSpace(_folderPath)) return;
        try
        {
            var path = _folderPath;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开文件夹：\n" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
