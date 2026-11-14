using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen;

/// <summary>设备管理「投影到电脑」：居中卡片（对齐 Win RemoteBindDialog）。</summary>
public sealed class RemoteProjectDialogPage : ContentPage
{
    private readonly TaskCompletionSource<RemoteProjectChoice?> _tcs = new();
    private readonly Picker _targetPicker = new();
    private readonly Picker _resolutionPicker = new();
    private readonly Picker _bitratePicker = new();
    private readonly Picker _qualityPicker = new();
    private readonly IReadOnlyList<RemoteBindingHelper.LayoutTarget> _targets;
    private readonly List<(int W, int H)> _resolutions = [];
    private readonly List<int> _bitrates = [];
    private readonly List<int> _qualities = [];

    public RemoteProjectDialogPage(
        string remoteName,
        IReadOnlyList<RemoteBindingHelper.LayoutTarget> targets,
        string? currentDeviceId,
        AppSettings settings)
    {
        _targets = targets;
        Title = Loc.T("Net.BindTitle");
        BackgroundColor = Color.FromArgb("#80000000");

        foreach (var t in targets)
            _targetPicker.Items.Add(t.Label);
        var tIdx = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            if (string.Equals(targets[i].DeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                tIdx = i;
                break;
            }
        }
        _targetPicker.SelectedIndex = tIdx;

        _resolutions.Add((0, 0));
        _resolutionPicker.Items.Add(ProjectionDisplayPresets.ResolutionLabel(0, 0));
        foreach (var (w, h) in ProjectionDisplayPresets.Resolutions)
        {
            _resolutions.Add((w, h));
            _resolutionPicker.Items.Add(ProjectionDisplayPresets.ResolutionLabel(w, h));
        }
        var rw = settings.ScreenExtendWidth > 0 ? settings.ScreenExtendWidth : 1920;
        var rh = settings.ScreenExtendHeight > 0 ? settings.ScreenExtendHeight : 1080;
        var rIdx = _resolutions.FindIndex(r => r.W == rw && r.H == rh);
        if (rIdx < 0 && rw > 0 && rh > 0)
        {
            _resolutions.Add((rw, rh));
            _resolutionPicker.Items.Add(ProjectionDisplayPresets.ResolutionLabel(rw, rh));
            rIdx = _resolutions.Count - 1;
        }
        _resolutionPicker.SelectedIndex = rIdx >= 0 ? rIdx : 0;

        foreach (var fps in ProjectionDisplayPresets.FrameRates)
        {
            _bitrates.Add(fps);
            _bitratePicker.Items.Add(ProjectionDisplayPresets.BitrateLabel(fps));
        }
        _bitratePicker.SelectedIndex = IndexOf(_bitrates, ProjectionDisplayPresets.RememberedFps(settings));

        foreach (var q in ProjectionDisplayPresets.JpegQualities)
        {
            _qualities.Add(q);
            _qualityPicker.Items.Add(ProjectionDisplayPresets.JpegQualityLabel(q));
        }
        _qualityPicker.SelectedIndex = IndexOf(_qualities, ProjectionDisplayPresets.RememberedJpegQuality(settings));

        StyleField(_targetPicker);
        StyleField(_resolutionPicker);
        StyleField(_bitratePicker);
        StyleField(_qualityPicker);

        var cancel = new Button
        {
            Text = Loc.T("Dlg.Cancel"),
            BackgroundColor = Color.FromArgb("#2A2A2E"),
            TextColor = Colors.White,
            CornerRadius = 8,
            Padding = new Thickness(18, 10),
            MinimumWidthRequest = 88
        };
        cancel.Clicked += async (_, _) =>
        {
            _tcs.TrySetResult(null);
            await Navigation.PopModalAsync();
        };
        var ok = new Button
        {
            Text = Loc.T("Net.BindOk"),
            BackgroundColor = Color.FromArgb("#2A2A2E"),
            TextColor = Colors.White,
            CornerRadius = 8,
            Padding = new Thickness(18, 10),
            MinimumWidthRequest = 88
        };
        ok.Clicked += async (_, _) =>
        {
            _tcs.TrySetResult(BuildChoice());
            await Navigation.PopModalAsync();
        };

        var card = new Border
        {
            Stroke = Color.FromArgb("#3F3F46"),
            StrokeThickness = 1,
            BackgroundColor = Color.FromArgb("#1A1A1D"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(22, 20),
            MaximumWidthRequest = 440,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    new Label
                    {
                        Text = Loc.T("Net.BindTitle"),
                        FontSize = 20,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White
                    },
                    new Label
                    {
                        Text = string.Format(Loc.T("Net.BindHint"), remoteName),
                        LineBreakMode = LineBreakMode.WordWrap,
                        TextColor = Color.FromArgb("#9CA3AF"),
                        FontSize = 13,
                        Margin = new Thickness(0, 0, 0, 6)
                    },
                    FieldLabel(Loc.T("Net.BindTarget")),
                    _targetPicker,
                    FieldLabel(Loc.T("Net.BindResolution")),
                    _resolutionPicker,
                    FieldLabel(Loc.T("Net.BindBitrate")),
                    _bitratePicker,
                    FieldLabel(Loc.T("Net.BindQuality")),
                    _qualityPicker,
                    new HorizontalStackLayout
                    {
                        Spacing = 12,
                        HorizontalOptions = LayoutOptions.End,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { cancel, ok }
                    }
                }
            }
        };

        // 点击遮罩空白处不关闭（避免误触）；内容居中卡片
        Content = new Grid
        {
            Children =
            {
                new BoxView { Color = Color.FromArgb("#99000000") },
                new ScrollView
                {
                    VerticalOptions = LayoutOptions.Fill,
                    Content = new Grid
                    {
                        Padding = new Thickness(24),
                        Children = { card }
                    }
                }
            }
        };
    }

    public Task<RemoteProjectChoice?> WaitAsync() => _tcs.Task;

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        FontAttributes = FontAttributes.Bold,
        TextColor = Colors.White,
        FontSize = 13,
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static void StyleField(Picker picker)
    {
        picker.BackgroundColor = Color.FromArgb("#25252B");
        picker.TextColor = Colors.White;
        picker.TitleColor = Color.FromArgb("#9CA3AF");
        picker.HeightRequest = 40;
    }

    private RemoteProjectChoice BuildChoice()
    {
        var tIdx = Math.Clamp(_targetPicker.SelectedIndex, 0, _targets.Count - 1);
        var rIdx = Math.Clamp(_resolutionPicker.SelectedIndex, 0, _resolutions.Count - 1);
        var bIdx = Math.Clamp(_bitratePicker.SelectedIndex, 0, _bitrates.Count - 1);
        var qIdx = Math.Clamp(_qualityPicker.SelectedIndex, 0, _qualities.Count - 1);
        var (w, h) = _resolutions[rIdx];
        return new RemoteProjectChoice
        {
            DeviceId = _targets[tIdx].DeviceId,
            Width = w,
            Height = h,
            Fps = _bitrates[bIdx],
            JpegQuality = _qualities[qIdx]
        };
    }

    private static int IndexOf(IReadOnlyList<int> values, int want)
    {
        for (var i = 0; i < values.Count; i++)
            if (values[i] == want) return i;
        return values.Count > 0 ? 0 : -1;
    }
}
