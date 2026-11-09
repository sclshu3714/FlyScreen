using FlyScreen.AppMaui.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Services.Tools.ColorPicker;
using Microsoft.Maui.Controls.Shapes;
#if WINDOWS
using FlyScreen.Tools;
#endif

namespace FlyScreen.AppMaui;

/// <summary>颜色拾取器内嵌面板（工具工作区）。</summary>
public sealed class ColorPickerPanel : ContentView
{
    private readonly ColorPickerService _service;
    private readonly BoxView _preview;
    private readonly Label _hex;
    private readonly Button _pickBtn;
    private readonly VerticalStackLayout _formats;
    private readonly Label _status;
#if WINDOWS
    private WinEyedropperSession? _session;
#endif

    public ColorPickerPanel()
    {
        _service = ToolService.Instance.ColorPicker;
        var dark = MauiThemeService.IsDark(MauiThemeService.Current);
        var text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        var muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        var line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        var subtle = Color.FromArgb(dark ? "#0F172A" : "#F3F4F6");
        var blue = Color.FromArgb("#2563EB");

        _preview = new BoxView
        {
            WidthRequest = 72,
            HeightRequest = 72,
            CornerRadius = 10,
            Color = Color.FromRgb(37, 99, 235)
        };
        _hex = new Label { FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = text, FontFamily = "Consolas" };
        _pickBtn = new Button
        {
            Text = Loc.T("Tool.ColorPicker.Pick"),
            Padding = new Thickness(14, 8),
            HorizontalOptions = LayoutOptions.Start
        };
        if (Application.Current?.Resources.TryGetValue("PrimaryBtn", out var pickStyle) == true
            && pickStyle is Style primaryBtn)
        {
            _pickBtn.Style = primaryBtn;
        }
        else
        {
            _pickBtn.BackgroundColor = blue;
            _pickBtn.TextColor = Colors.White;
        }
        _pickBtn.Clicked += OnPickClicked;
        _formats = new VerticalStackLayout { Spacing = 6 };
        _status = new Label { FontSize = 12, TextColor = muted, Margin = new Thickness(0, 8, 0, 0) };

        var tip = new Label
        {
            Text = Loc.T("Tool.ColorPicker.Hint"),
            FontSize = 12,
            TextColor = muted,
            LineBreakMode = LineBreakMode.WordWrap
        };

        var header = new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                tip,
                new HorizontalStackLayout
                {
                    Spacing = 14,
                    Children =
                    {
                        new Border
                        {
                            Stroke = line,
                            StrokeThickness = 1,
                            StrokeShape = new RoundRectangle { CornerRadius = 10 },
                            Content = _preview
                        },
                        new VerticalStackLayout
                        {
                            VerticalOptions = LayoutOptions.Center,
                            Spacing = 6,
                            Children =
                            {
                                new Label { Text = Loc.T("Tool.ColorPicker.Preview"), FontSize = 12, TextColor = muted },
                                _hex,
                                _pickBtn
                            }
                        }
                    }
                }
            }
        };

        var formatsTitle = new Label
        {
            Text = Loc.T("Tool.ColorPicker.Formats"),
            FontAttributes = FontAttributes.Bold,
            FontSize = 13,
            TextColor = text,
            Margin = new Thickness(0, 12, 0, 8)
        };

        var formatsScroll = new ScrollView
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Default,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children = { _formats, _status }
            }
        };

        // 上方预览区固定，仅「颜色格式」列表滚动
        var root = new Grid
        {
            Padding = new Thickness(4, 0, 4, 8),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            }
        };
        root.Add(header);
        root.Add(formatsTitle, 0, 1);
        root.Add(formatsScroll, 0, 2);
        Content = root;

        _ = subtle;
        Loaded += (_, _) =>
        {
            _service.ColorChanged += OnColorChanged;
            _service.SessionChanged += OnSessionChanged;
            RefreshPreviewOnly();
            RefreshFormats();
            UpdatePickButton();
        };
        Unloaded += (_, _) =>
        {
            _service.ColorChanged -= OnColorChanged;
            _service.SessionChanged -= OnSessionChanged;
            StopEyedropper();
        };
        RefreshPreviewOnly();
        RefreshFormats();
        UpdatePickButton();
    }

    private void OnColorChanged()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            RefreshPreviewOnly();
            if (!_service.IsPicking)
                RefreshFormats();
        });

    private void OnSessionChanged()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdatePickButton();
            if (!_service.IsPicking)
                RefreshFormats();
        });

    private void UpdatePickButton()
    {
        _pickBtn.Text = _service.IsPicking
            ? Loc.T("Tool.ColorPicker.Picking")
            : Loc.T("Tool.ColorPicker.Pick");
    }

    private void RefreshPreviewOnly()
    {
        var c = _service.Current;
        _preview.Color = Color.FromRgba(c.R, c.G, c.B, c.A);
        _hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private void RefreshFormats()
    {
        var c = _service.Current;
        var dark = MauiThemeService.IsDark(MauiThemeService.Current);
        var text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        var muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        var line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        var subtle = Color.FromArgb(dark ? "#0F172A" : "#F3F4F6");

        _formats.Children.Clear();
        foreach (var entry in ColorFormatConverter.ConvertAll(c))
        {
            var nameLabel = new Label
            {
                Text = entry.Label,
                FontSize = 12,
                TextColor = muted,
                VerticalOptions = LayoutOptions.Center
            };
            var valueLabel = new Label
            {
                Text = entry.Value,
                FontSize = 12,
                TextColor = text,
                FontFamily = "Consolas",
                LineBreakMode = LineBreakMode.WordWrap,
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(valueLabel, 1);
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(140)),
                    new(GridLength.Star)
                }
            };
            grid.Children.Add(nameLabel);
            grid.Children.Add(valueLabel);
            var row = new Border
            {
                Stroke = line,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                BackgroundColor = subtle,
                Padding = new Thickness(10, 8),
                Content = grid
            };
            var tap = new TapGestureRecognizer();
            var captured = entry;
            tap.Tapped += async (_, _) =>
            {
                try
                {
                    await Clipboard.Default.SetTextAsync(captured.Value);
                    _status.Text = Loc.T("Tool.ColorPicker.Copied") + " · " + captured.Label;
                }
                catch { /* ignore */ }
            };
            row.GestureRecognizers.Add(tap);
            _formats.Children.Add(row);
        }
    }

    private void OnPickClicked(object? sender, EventArgs e)
    {
        if (_service.IsPicking)
        {
            StopEyedropper();
            _service.CancelPicking();
            return;
        }

#if WINDOWS
        if (_service.Sampler == null)
            ToolService.Instance.AttachColorSampler(new WinScreenColorSampler());

        _service.BeginPicking();
        StopEyedropper();
        _session = new WinEyedropperSession(
            _service.Sampler!,
            onMove: color => _service.UpdatePreview(color),
            onCommit: color => MainThread.BeginInvokeOnMainThread(() =>
            {
                StopEyedropper();
                _service.Commit(color);
            }),
            onCancel: () => MainThread.BeginInvokeOnMainThread(() =>
            {
                StopEyedropper();
                _service.CancelPicking();
            }));
        UpdatePickButton();
#else
        _status.Text = Loc.T("Tool.ColorPicker.Unsupported");
#endif
    }

    private void StopEyedropper()
    {
#if WINDOWS
        _session?.Dispose();
        _session = null;
#endif
    }
}
