using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.Tool.FileVault.Ui.Desktop;

internal static class FileVaultDialogs
{
    public static async Task<string?> PromptPassword(Window owner)
    {
        var win = new Window
        {
            Title = Loc.T("Tool.FileVault.Password"),
            Width = 360,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var box = new TextBox { PasswordChar = '•', Margin = new Avalonia.Thickness(16) };
        string? result = null;
        var ok = new Button { Content = Loc.T("Tool.FileVault.Ok"), Width = 80 };
        ok.Click += (_, _) => { result = box.Text; win.Close(); };
        win.Content = new StackPanel
        {
            Children =
            {
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Avalonia.Thickness(16),
                    Children = { ok }
                }
            }
        };
        await win.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> PromptText(Window owner, string message)
    {
        var win = new Window
        {
            Title = Loc.T("Tool.FileVault.Name"),
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var box = new TextBox { Margin = new Avalonia.Thickness(16, 8, 16, 8) };
        string? result = null;
        var ok = new Button { Content = Loc.T("Tool.FileVault.Ok"), Width = 80 };
        ok.Click += (_, _) => { result = box.Text; win.Close(); };
        win.Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(16, 16, 16, 0) },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Avalonia.Thickness(16),
                    Children = { ok }
                }
            }
        };
        await win.ShowDialog(owner);
        return result;
    }
}
