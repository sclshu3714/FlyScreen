using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.Tool.FileVault.Ui.Win;

internal static class FileVaultPasswordDialog
{
    public static string? Show(Window? owner, string? title)
    {
        var win = new Window
        {
            Title = title ?? Loc.T("Tool.FileVault.Password"),
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            ShowInTaskbar = false
        };

        var pwd = new PasswordBox { Margin = new Thickness(16, 16, 16, 8) };
        var ok = new Button
        {
            Content = Loc.T("Tool.FileVault.Ok"),
            IsDefault = true,
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancel = new Button
        {
            Content = Loc.T("Tool.FileVault.Cancel"),
            IsCancel = true,
            Width = 80
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = pwd.Password;
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 8, 16, 16),
            Children = { ok, cancel }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        win.Content = new DockPanel
        {
            LastChildFill = true,
            Children = { buttons, pwd }
        };

        return win.ShowDialog() == true ? result : null;
    }
}
