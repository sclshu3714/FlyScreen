using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.DesktopUi.Dialogs;

/// <summary>缺解锁密码时的设备/密码表格弹窗（Avalonia）。</summary>
public sealed class UnlockPasswordsWindow : Window
{
    private readonly Dictionary<string, TextBox> _boxes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string>? Result { get; private set; }

    public UnlockPasswordsWindow(IReadOnlyList<(string MachineName, string Label)> machines)
    {
        Title = Loc.T("Tray.UnlockPasswords.Title");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC));

        var root = new StackPanel { Margin = new Thickness(16) };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headDevice = new TextBlock
        {
            Text = Loc.T("Tray.UnlockPasswords.Device"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 8, 8)
        };
        var headPass = new TextBlock
        {
            Text = Loc.T("Tray.UnlockPasswords.Password"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetColumn(headDevice, 0);
        Grid.SetColumn(headPass, 1);
        grid.Children.Add(headDevice);
        grid.Children.Add(headPass);

        for (var i = 0; i < machines.Count; i++)
        {
            var (name, label) = machines[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = i + 1;

            var device = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 8),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(device, row);
            Grid.SetColumn(device, 0);
            grid.Children.Add(device);

            var box = new TextBox
            {
                Height = 32,
                Margin = new Thickness(0, 0, 0, 8),
                PasswordChar = '●',
                Watermark = Loc.T("Tray.UnlockPasswords.Password")
            };
            _boxes[name] = box;
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
        }

        root.Children.Add(grid);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var ok = new Button
        {
            Content = Loc.T("Tray.UnlockPasswords.Ok"),
            MinWidth = 88,
            Height = 32
        };
        var cancel = new Button
        {
            Content = Loc.T("Tray.UnlockPasswords.Cancel"),
            MinWidth = 88,
            Height = 32
        };
        ok.Click += (_, _) =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, box) in _boxes)
            {
                var pwd = box.Text ?? "";
                if (string.IsNullOrEmpty(pwd)) continue;
                map[name] = pwd;
            }

            if (map.Count == 0) return;
            Result = map;
            Close(Result);
        };
        cancel.Click += (_, _) =>
        {
            Result = null;
            Close(null);
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        if (_boxes.Count > 0)
            Opened += (_, _) => _boxes.Values.First().Focus();
    }

    public static async Task<IReadOnlyDictionary<string, string>?> PromptAsync(
        Window? owner,
        IReadOnlyList<(string MachineName, string Label)> machines)
    {
        if (machines.Count == 0)
            return new Dictionary<string, string>();

        var dlg = new UnlockPasswordsWindow(machines);
        if (owner != null)
            return await dlg.ShowDialog<IReadOnlyDictionary<string, string>?>(owner).ConfigureAwait(true);

        dlg.Show();
        return dlg.Result;
    }
}
