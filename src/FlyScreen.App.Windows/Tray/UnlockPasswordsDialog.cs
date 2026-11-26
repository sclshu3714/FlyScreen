using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlyScreen.Helpers.Localization;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace FlyScreen.Tray;

/// <summary>缺解锁密码时的设备/密码表格弹窗。</summary>
public sealed class UnlockPasswordsDialog : Window
{
    private readonly Dictionary<string, PasswordBox> _boxes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string>? Result { get; private set; }

    public UnlockPasswordsDialog(IReadOnlyList<(string MachineName, string Label)> machines)
    {
        Title = Loc.T("Tray.UnlockPasswords.Title");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(WpfColor.FromRgb(0xF7, 0xF9, 0xFC));

        var root = new StackPanel { Margin = new Thickness(16) };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headDevice = new TextBlock
        {
            Text = Loc.T("Tray.UnlockPasswords.Device"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 8)
        };
        var headPass = new TextBlock
        {
            Text = Loc.T("Tray.UnlockPasswords.Password"),
            FontWeight = FontWeights.SemiBold,
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

            var box = new PasswordBox
            {
                Height = 32,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(8, 4, 8, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _boxes[name] = box;
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
        }

        root.Children.Add(grid);

        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var ok = new WpfButton
        {
            Content = Loc.T("Tray.UnlockPasswords.Ok"),
            MinWidth = 88,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        var cancel = new WpfButton
        {
            Content = Loc.T("Tray.UnlockPasswords.Cancel"),
            MinWidth = 88,
            Height = 32,
            IsCancel = true
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
            DialogResult = true;
            Close();
        };
        cancel.Click += (_, _) =>
        {
            Result = null;
            DialogResult = false;
            Close();
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        if (_boxes.Count > 0)
            Loaded += (_, _) => _boxes.Values.First().Focus();
    }

    /// <summary>弹出密码表；确定返回 MachineName→Password，取消返回 null。</summary>
    public static IReadOnlyDictionary<string, string>? Prompt(
        IReadOnlyList<(string MachineName, string Label)> machines,
        Window? owner = null)
    {
        if (machines.Count == 0)
            return new Dictionary<string, string>();

        var dlg = new UnlockPasswordsDialog(machines);
        // 仅当 Owner 可见时挂接；隐藏主窗作 Owner 时 WPF ShowDialog 常直接失败
        if (owner is { IsVisible: true, IsLoaded: true })
            dlg.Owner = owner;
        dlg.ShowInTaskbar = true;
        dlg.Activate();
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
