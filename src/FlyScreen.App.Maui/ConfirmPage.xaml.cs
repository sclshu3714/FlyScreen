using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.AppMaui;

/// <summary>主题化确认页（模态，对齐 Win ConfirmDialog）。</summary>
public partial class ConfirmPage : ContentPage
{
    private readonly TaskCompletionSource<UiConfirmChoice> _tcs = new();

    public Task<UiConfirmChoice> Result => _tcs.Task;

    public ConfirmPage(
        string title,
        string message,
        string? primaryText = null,
        string? secondaryText = null,
        string? cancelText = null)
    {
        InitializeComponent();
        Title = title;
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        PrimaryBtn.Text = string.IsNullOrWhiteSpace(primaryText) ? Loc.T("Dlg.Ok") : primaryText;
        CancelBtn.Text = string.IsNullOrWhiteSpace(cancelText) ? Loc.T("Dlg.Cancel") : cancelText;

        if (string.IsNullOrWhiteSpace(secondaryText))
        {
            SecondaryBtn.IsVisible = false;
        }
        else
        {
            SecondaryBtn.Text = secondaryText;
            SecondaryBtn.IsVisible = true;
        }
    }

    public static ConfirmPage ForTransferDelete(string title, string message)
        => new(title, message,
            Loc.T("Xfer.DeleteFiles"),
            Loc.T("Xfer.KeepFiles"),
            Loc.T("Dlg.Cancel"));

    private async void Primary_Clicked(object? sender, EventArgs e)
        => await FinishAsync(UiConfirmChoice.Primary);

    private async void Secondary_Clicked(object? sender, EventArgs e)
        => await FinishAsync(UiConfirmChoice.Secondary);

    private async void Cancel_Clicked(object? sender, EventArgs e)
        => await FinishAsync(UiConfirmChoice.Cancel);

    private async Task FinishAsync(UiConfirmChoice choice)
    {
        _tcs.TrySetResult(choice);
        try { await Navigation.PopModalAsync(); }
        catch { /* ignore */ }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tcs.TrySetResult(UiConfirmChoice.Cancel);
    }
}
