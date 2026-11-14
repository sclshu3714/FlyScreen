using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.ColorPicker;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.ColorPicker.Ui.Desktop;

public partial class ColorPickerPanel : UserControl
{
    private readonly IColorPickerService _service;
    private DesktopEyedropperSession? _session;
    private bool _formatsDirty = true;

    public Action<string>? StatusReporter { get; set; }

    public ColorPickerPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.ColorPicker
            ?? throw new InvalidOperationException("颜色拾取器未安装或未加载");
        AttachedToVisualTree += (_, _) =>
        {
            _service.ColorChanged += OnColorChanged;
            _service.SessionChanged += OnSessionChanged;
            RefreshPreviewOnly();
            RefreshFormats();
            UpdatePickButton();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _service.ColorChanged -= OnColorChanged;
            _service.SessionChanged -= OnSessionChanged;
            StopEyedropper();
        };
        ApplyLoc();
        RefreshPreviewOnly();
        RefreshFormats();
    }

    private void OnColorChanged()
        => Dispatcher.UIThread.Post(() =>
        {
            RefreshPreviewOnly();
            if (!_service.IsPicking)
                RefreshFormats();
            else
                _formatsDirty = true;
        });

    private void OnSessionChanged()
        => Dispatcher.UIThread.Post(() =>
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
        PreviewBorder.Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        PreviewHex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private void RefreshFormats()
    {
        FormatsList.ItemsSource = ColorFormatConverter.ConvertAll(_service.Current);
        _formatsDirty = false;
    }

    private void PickBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_service.IsPicking)
        {
            StopEyedropper();
            _service.CancelPicking();
            return;
        }

        if (_service.Sampler == null)
        {
            // 采样器由宿主注入；工具 UI 不创建平台采样实现
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;

        _service.BeginPicking();
        StopEyedropper();
        _session = new DesktopEyedropperSession(
            owner,
            _service.Sampler,
            onMove: color => _service.UpdatePreview(color),
            onCommit: color => Dispatcher.UIThread.Post(() =>
            {
                StopEyedropper();
                _service.Commit(color);
                RefreshFormats();
            }),
            onCancel: () => Dispatcher.UIThread.Post(() =>
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

    private async void FormatRow_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control { Tag: ColorFormatEntry entry }) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard != null)
                await top.Clipboard.SetTextAsync(entry.Value);
            StatusReporter?.Invoke(Loc.T("Tool.ColorPicker.Copied") + " · " + entry.Label);
        }
        catch
        {
            /* ignore */
        }
    }
}

