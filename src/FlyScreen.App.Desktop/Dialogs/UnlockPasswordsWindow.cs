using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FlyScreen.Helpers.Localization;
using FlyScreen.Ui.Avalonia;

namespace FlyScreen.DesktopUi.Dialogs;

/// <summary>缺解锁密码时的设备/密码小窗（独立 Show，不挂主窗 Owner）。</summary>
public sealed class UnlockPasswordsWindow : Window
{
    private readonly Dictionary<string, SecurePasswordBox> _boxes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string>? Result { get; private set; }

    public UnlockPasswordsWindow(IReadOnlyList<(string MachineName, string Label)> machines)
    {
        Title = Loc.T("Tray.UnlockPasswords.Title");
        Width = 390;
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        MaxWidth = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        ShowInTaskbar = true;
        Topmost = true;
        SystemDecorations = SystemDecorations.Full;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC));

        var root = new StackPanel { Margin = new Thickness(14) };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headDevice = new TextBlock
        {
            Text = Loc.T("Tray.UnlockPasswords.Device"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 8, 6)
        };
        var headPass = new TextBlock
        {
            Text = Loc.T("Tray.UnlockPasswords.Password"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
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
                Margin = new Thickness(0, 0, 8, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 124
            };
            Grid.SetRow(device, row);
            Grid.SetColumn(device, 0);
            grid.Children.Add(device);

            var box = new SecurePasswordBox
            {
                Height = 36,
                Width = 200,
                Margin = new Thickness(0, 0, 0, 6),
                Placeholder = Loc.T("Tray.UnlockPasswords.Password"),
                ShowPasswordTip = Loc.T("Sec.ShowPassword"),
                HidePasswordTip = Loc.T("Sec.HidePassword")
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
            MinWidth = 72,
            Height = 28
        };
        var cancel = new Button
        {
            Content = Loc.T("Tray.UnlockPasswords.Cancel"),
            MinWidth = 72,
            Height = 28
        };
        ok.Click += (_, _) =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, box) in _boxes)
            {
                var pwd = box.Password ?? "";
                if (string.IsNullOrEmpty(pwd)) continue;
                map[name] = pwd;
            }

            if (map.Count == 0) return;
            Result = map;
            Close();
        };
        cancel.Click += (_, _) =>
        {
            Result = null;
            Close();
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        if (_boxes.Count > 0)
            Opened += (_, _) => _boxes.Values.First().Focus();
    }

    public static Task<IReadOnlyDictionary<string, string>?> PromptAsync(
        Window? owner,
        IReadOnlyList<(string MachineName, string Label)> machines)
    {
        _ = owner;
        if (machines.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>());

        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, string>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dlg = new UnlockPasswordsWindow(machines);
        var appMain = Application.Current?.ApplicationLifetime as Window;
        var keepHidden = appMain is { IsVisible: false };

        dlg.Closed += (_, _) =>
        {
            try
            {
                if (keepHidden && appMain is { IsVisible: true })
                    appMain.Hide();
            }
            catch { /* ignore */ }

            tcs.TrySetResult(dlg.Result);
        };
        dlg.Show();
        return tcs.Task;
    }
}
