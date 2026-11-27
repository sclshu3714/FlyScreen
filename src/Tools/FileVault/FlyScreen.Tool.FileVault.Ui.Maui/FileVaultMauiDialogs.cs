namespace FlyScreen.Tool.FileVault.Ui.Maui;

internal static class FileVaultMauiDialogs
{
    public static async Task<string?> PickFolderAsync(string title)
    {
#if WINDOWS
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = ((MauiWinUIWindow)Application.Current!.Windows[0].Handler!.PlatformView!).WindowHandle;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch
        {
            return null;
        }
#else
        await Task.CompletedTask;
        _ = title;
        return null;
#endif
    }
}
