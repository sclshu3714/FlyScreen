using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.DesktopUi;

/// <summary>主题化确认框（对齐主窗口样式）。</summary>
public partial class ConfirmDialog : Window
{
    public UiConfirmChoice Choice { get; private set; } = UiConfirmChoice.Cancel;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static async Task<UiConfirmChoice> ShowAsync(
        Window owner,
        string title,
        string message,
        string? primaryText = null,
        string? secondaryText = null,
        string? cancelText = null)
    {
        var dlg = new ConfirmDialog
        {
            Title = title
        };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.PrimaryBtn.Content = string.IsNullOrWhiteSpace(primaryText) ? Loc.T("Dlg.Ok") : primaryText;
        dlg.CancelBtn.Content = string.IsNullOrWhiteSpace(cancelText) ? Loc.T("Dlg.Cancel") : cancelText;

        if (string.IsNullOrWhiteSpace(secondaryText))
        {
            dlg.SecondaryBtn.IsVisible = false;
        }
        else
        {
            dlg.SecondaryBtn.Content = secondaryText;
            dlg.SecondaryBtn.IsVisible = true;
        }

        await dlg.ShowDialog(owner).ConfigureAwait(true);
        return dlg.Choice;
    }

    public static Task<UiConfirmChoice> ShowTransferDeleteAsync(Window owner, string title, string message)
        => ShowAsync(owner, title, message,
            Loc.T("Xfer.DeleteFiles"),
            Loc.T("Xfer.KeepFiles"),
            Loc.T("Dlg.Cancel"));

    private void Primary_Click(object? sender, RoutedEventArgs e)
    {
        Choice = UiConfirmChoice.Primary;
        Close();
    }

    private void Secondary_Click(object? sender, RoutedEventArgs e)
    {
        Choice = UiConfirmChoice.Secondary;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Choice = UiConfirmChoice.Cancel;
        Close();
    }
}
