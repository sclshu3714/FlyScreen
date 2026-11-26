using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Ui.Theme.Wpf;
using FlyScreen.Ui.Wpf;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace FlyScreen.Tray;

/// <summary>
/// 解锁密码维护小窗。独立 HWND，不挂主窗 Owner，不用 ShowDialog（避免托盘主窗被拉起/样式异常）。
/// </summary>
public sealed class UnlockPasswordsDialog : Window
{
    private static int _promptOpen;

    private readonly Dictionary<string, SecurePasswordBox> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _accepted;

    public IReadOnlyDictionary<string, string>? Result { get; private set; }

    public UnlockPasswordsDialog(IReadOnlyList<(string MachineName, string Label)> machines)
    {
        // 切断与主窗主题/模板的关联（避免托盘主窗铬），颜色走弹窗实心色板
        Style = null;
        OverridesDefaultStyle = false;

        Title = Loc.T("Tray.UnlockPasswords.Title");
        Width = 390;
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        MaxWidth = 440;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        ShowActivated = true;
        Topmost = true;
        WindowStyle = WindowStyle.ToolWindow; // 无最大/最小化；系统标题栏，无主窗「投影」
        AllowsTransparency = false;
        FontFamily = new WpfFontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;

        WpfDialogTheme.ApplyTo(this);
        var textBrush = BrushOr("Text", WpfBrushes.Black);
        var mutedBrush = BrushOr("Muted", new SolidColorBrush(WpfColor.FromRgb(0x6B, 0x72, 0x80)));

        var root = new StackPanel { Margin = new Thickness(14) };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headDevice = new TextBlock
        {
            Text = Loc.T("Tray.UnlockPasswords.Device"),
            FontWeight = FontWeights.SemiBold,
            Foreground = mutedBrush,
            Margin = new Thickness(0, 0, 8, 6)
        };
        var headPass = new TextBlock
        {
            Text = Loc.T("Tray.UnlockPasswords.Password"),
            FontWeight = FontWeights.SemiBold,
            Foreground = mutedBrush,
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
                Foreground = textBrush,
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
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var ok = new WpfButton
        {
            Content = Loc.T("Tray.UnlockPasswords.Ok"),
            MinWidth = 72,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        var cancel = new WpfButton
        {
            Content = Loc.T("Tray.UnlockPasswords.Cancel"),
            MinWidth = 72,
            Height = 28,
            IsCancel = true
        };
        ApplyButtonStyle(ok, "DlgPrimaryBtn");
        ApplyButtonStyle(cancel, "DlgNormalBtn");
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
            _accepted = true;
            Close();
        };
        cancel.Click += (_, _) =>
        {
            Result = null;
            _accepted = false;
            Close();
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) =>
        {
            StripMinMaxButtons();
            CenterOnScreen();
        };
        Loaded += (_, _) =>
        {
            WpfDialogTheme.ApplyTo(this);
            foreach (var box in _boxes.Values)
                box.ApplyThemeFromResources();
            if (_boxes.Count > 0)
                _boxes.Values.First().FocusInput();
        };
    }

    private WpfBrush BrushOr(string key, WpfBrush fallback)
        => TryFindResource(key) as WpfBrush ?? fallback;

    private void ApplyButtonStyle(WpfButton button, string key)
    {
        if (TryFindResource(key) is Style style)
            button.Style = style;
    }

    private void StripMinMaxButtons()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var style = GetWindowLong(hwnd, GwlStyle);
            style &= ~(WsMinimizeBox | WsMaximizeBox);
            SetWindowLong(hwnd, GwlStyle, style);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }
        catch
        {
            // ignore
        }
    }

    private void CenterOnScreen()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + (area.Height - ActualHeight) / 2;
            if (double.IsNaN(Left) || double.IsNaN(Top))
            {
                Left = area.Left + 80;
                Top = area.Top + 80;
            }
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private const int GwlStyle = -16;
    private const int WsMinimizeBox = 0x20000;
    private const int WsMaximizeBox = 0x10000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    /// <summary>
    /// 独立模态：临时把 Application.MainWindow 指到本窗，避免 ShowDialog 拉起托盘主窗。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Prompt(
        IReadOnlyList<(string MachineName, string Label)> machines,
        Window? owner = null)
    {
        _ = owner;
        if (machines.Count == 0)
            return new Dictionary<string, string>();

        // 禁止多开：连点托盘会叠多个密码窗，确认后重复注入导致对端「密码错误」
        if (Interlocked.CompareExchange(ref _promptOpen, 1, 0) != 0)
            return null;

        var app = Application.Current;
        var realMain = app?.MainWindow;
        var mainWasVisible = realMain?.IsVisible == true;
        var mainShowInTaskbar = realMain?.ShowInTaskbar ?? false;
        var mainState = realMain?.WindowState ?? WindowState.Normal;

        // 先确保真主窗保持托盘隐藏，不被模态逻辑唤醒
        if (realMain != null && !mainWasVisible)
        {
            try
            {
                realMain.ShowInTaskbar = false;
                realMain.Visibility = Visibility.Hidden;
            }
            catch { /* ignore */ }
        }

        var dlg = new UnlockPasswordsDialog(machines);
        if (app != null)
            app.MainWindow = dlg;

        try
        {
            // 不用 ShowDialog：改用 Show + DispatcherFrame，主窗不会被当成 Owner 激活
            var frame = new DispatcherFrame();
            dlg.Closed += (_, _) => { frame.Continue = false; };
            dlg.Show();
            Dispatcher.PushFrame(frame);
            return dlg._accepted ? dlg.Result : null;
        }
        finally
        {
            Interlocked.Exchange(ref _promptOpen, 0);
            if (app != null && realMain != null)
            {
                try
                {
                    app.MainWindow = realMain;
                    if (!mainWasVisible)
                    {
                        realMain.ShowInTaskbar = false;
                        realMain.Visibility = Visibility.Hidden;
                        if (realMain.WindowState != WindowState.Minimized)
                            realMain.WindowState = WindowState.Minimized;
                    }
                    else
                    {
                        realMain.ShowInTaskbar = mainShowInTaskbar;
                        realMain.WindowState = mainState;
                    }
                }
                catch { /* ignore */ }
            }
        }
    }
}
