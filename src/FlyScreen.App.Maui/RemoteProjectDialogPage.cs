using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen;

/// <summary>设备管理「投影到电脑」：目标电脑 + 分辨率/码率/质量。</summary>
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

        var cancel = new Button { Text = Loc.T("Dlg.Cancel") };
        cancel.Clicked += async (_, _) =>
        {
            _tcs.TrySetResult(null);
            await Navigation.PopModalAsync();
        };
        var ok = new Button { Text = Loc.T("Net.BindOk") };
        ok.Clicked += async (_, _) =>
        {
            _tcs.TrySetResult(BuildChoice());
            await Navigation.PopModalAsync();
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 10,
                Children =
                {
                    new Label
                    {
                        Text = Loc.T("Net.BindTitle"),
                        FontSize = 18,
                        FontAttributes = FontAttributes.Bold
                    },
                    new Label
                    {
                        Text = string.Format(Loc.T("Net.BindHint"), remoteName),
                        LineBreakMode = LineBreakMode.WordWrap
                    },
                    new Label { Text = Loc.T("Net.BindTarget"), FontAttributes = FontAttributes.Bold },
                    _targetPicker,
                    new Label { Text = Loc.T("Net.BindResolution"), FontAttributes = FontAttributes.Bold },
                    _resolutionPicker,
                    new Label { Text = Loc.T("Net.BindBitrate"), FontAttributes = FontAttributes.Bold },
                    _bitratePicker,
                    new Label { Text = Loc.T("Net.BindQuality"), FontAttributes = FontAttributes.Bold },
                    _qualityPicker,
                    new HorizontalStackLayout
                    {
                        Spacing = 12,
                        HorizontalOptions = LayoutOptions.End,
                        Children = { cancel, ok }
                    }
                }
            }
        };
    }

    public Task<RemoteProjectChoice?> WaitAsync() => _tcs.Task;

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
