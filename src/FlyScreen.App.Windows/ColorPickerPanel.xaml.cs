using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Services.Tools.ColorPicker;
using FlyScreen.Tools;

namespace FlyScreen;

public partial class ColorPickerPanel : System.Windows.Controls.UserControl
{
    private readonly ColorPickerService _service;
    private WinEyedropperSession? _session;
    private bool _formatsDirty = true;

    public ColorPickerPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.ColorPicker;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyLoc();
        RefreshPreviewOnly();
        RefreshFormats();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _service.ColorChanged += OnColorChanged;
        _service.SessionChanged += OnSessionChanged;
        RefreshPreviewOnly();
        RefreshFormats();
        UpdatePickButton();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _service.ColorChanged -= OnColorChanged;
        _service.SessionChanged -= OnSessionChanged;
        StopEyedropper();
    }

    private void OnColorChanged()
        => Dispatcher.BeginInvoke(() =>
        {
            RefreshPreviewOnly();
            if (!_service.IsPicking)
                RefreshFormats();
            else
                _formatsDirty = true;
        });

    private void OnSessionChanged()
        => Dispatcher.BeginInvoke(() =>
        {
            UpdatePickButton();
            if (!_service.IsPicking && _formatsDirty)
                RefreshFormats();
        });

    private void ApplyLoc()
    {
        HintText.Text = Loc.T("Tool.ColorPicker.Hint");
        PreviewLabel.Text = Loc.T("Tool.ColorPicker.Preview");
        FormatsTitle.Text = Loc.T("Tool.ColorPicker.Formats");
        UpdatePickButton();
    }

    private void UpdatePickButton()
    {
        PickBtn.Content = _service.IsPicking
            ? Loc.T("Tool.ColorPicker.Picking")
            : Loc.T("Tool.ColorPicker.Pick");
    }

    private void RefreshPreviewOnly()
    {
        var c = _service.Current;
        PreviewBorder.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
        PreviewHex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private void RefreshFormats()
    {
        FormatsList.ItemsSource = ColorFormatConverter.ConvertAll(_service.Current);
        _formatsDirty = false;
    }

    private void PickBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service.IsPicking)
        {
            StopEyedropper();
            _service.CancelPicking();
            return;
        }

        if (_service.Sampler == null)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), Loc.T("Tool.ColorPicker.Unsupported"),
                Loc.T("Tool.ColorPicker.Name"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _service.BeginPicking();
        StopEyedropper();
        // 钩子回调尽量轻：只更新预览色，不在此处重建格式表
        _session = new WinEyedropperSession(
            _service.Sampler,
            onMove: color => _service.UpdatePreview(color),
            onCommit: color => Dispatcher.BeginInvoke(() =>
            {
                StopEyedropper();
                _service.Commit(color);
                RefreshFormats();
            }),
            onCancel: () => Dispatcher.BeginInvoke(() =>
            {
                StopEyedropper();
                _service.CancelPicking();
                RefreshFormats();
            }));
        UpdatePickButton();
    }

    private void StopEyedropper()
    {
        _session?.Dispose();
        _session = null;
    }

    private void FormatRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ColorFormatEntry entry }) return;
        try
        {
            System.Windows.Clipboard.SetText(entry.Value);
            if (Window.GetWindow(this) is MainWindow mw)
                mw.StatusText.Text = Loc.T("Tool.ColorPicker.Copied") + " · " + entry.Label;
        }
        catch
        {
            /* ignore */
        }
    }
}
