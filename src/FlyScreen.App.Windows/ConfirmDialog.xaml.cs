using System.Windows;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;

namespace FlyScreen;

/// <summary>主题化确认框（对齐主窗口样式，替代 MessageBox）。</summary>
public partial class ConfirmDialog : Window
{
    public UiConfirmChoice Choice { get; private set; } = UiConfirmChoice.Cancel;

    public ConfirmDialog()
    {
        InitializeComponent();
        ThemeService.ApplyToChildWindow(this);
        Loaded += (_, _) => ThemeService.ApplyToChildWindow(this);
    }

    /// <summary>
    /// 三选一：Primary / Secondary / Cancel。
    /// secondaryText 为 null 时隐藏次按钮（变成「确定 / 取消」）。
    /// </summary>
    public static UiConfirmChoice Show(
        Window? owner,
        string title,
        string message,
        string? primaryText = null,
        string? secondaryText = null,
        string? cancelText = null)
    {
        var dlg = new ConfirmDialog();
        if (owner != null)
            dlg.Owner = owner;

        dlg.Title = title;
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.PrimaryBtn.Content = string.IsNullOrWhiteSpace(primaryText) ? Loc.T("Dlg.Ok") : primaryText;
        dlg.CancelBtn.Content = string.IsNullOrWhiteSpace(cancelText) ? Loc.T("Dlg.Cancel") : cancelText;

        if (string.IsNullOrWhiteSpace(secondaryText))
        {
            dlg.SecondaryBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            dlg.SecondaryBtn.Content = secondaryText;
            dlg.SecondaryBtn.Visibility = Visibility.Visible;
        }

        dlg.ShowDialog();
        return dlg.Choice;
    }

    /// <summary>传输删除/清除：删除文件 / 仅删记录 / 取消。</summary>
    public static UiConfirmChoice ShowTransferDelete(Window? owner, string title, string message)
        => Show(owner, title, message,
            Loc.T("Xfer.DeleteFiles"),
            Loc.T("Xfer.KeepFiles"),
            Loc.T("Dlg.Cancel"));

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Choice = UiConfirmChoice.Primary;
        DialogResult = true;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Choice = UiConfirmChoice.Secondary;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = UiConfirmChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
