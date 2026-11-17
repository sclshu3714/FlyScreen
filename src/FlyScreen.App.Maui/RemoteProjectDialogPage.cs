using FlyScreen.Ui.Maui;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen;

/// <summary>设备管理「投影到电脑」：统一 MauiDialogShell（对齐 Win RemoteBindDialog）。</summary>
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
    private readonly MauiDialogShell _shell = new()
    {
        SizeMode = MauiDialogSize.Form,
        ShowClose = true
    };

    public RemoteProjectDialogPage(
        string remoteName,
        IReadOnlyList<RemoteBindingHelper.LayoutTarget> targets,
        string? currentDeviceId,
        AppSettings settings)
    {
        _targets = targets;
        Title = Loc.T("Net.BindTitle");
        FlyScreen.AppMaui.Helpers.MauiThemeService.PrepareOverlayPage(this);
        _shell.Title = Title;
        _shell.CloseRequested += async (_, _) => await FinishAsync(null);

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

        var cancel = MakeNormal(Loc.T("Dlg.Cancel"));
        cancel.Clicked += async (_, _) => await FinishAsync(null);
        var ok = MakePrimary(Loc.T("Net.BindOk"));
        ok.Clicked += async (_, _) => await FinishAsync(BuildChoice());

        _shell.Body = new ScrollView
        {
            Padding = new Thickness(20, 16, 20, 8),
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    new Label
                    {
                        Text = string.Format(Loc.T("Net.BindHint"), remoteName),
                        Style = TryStyle("DlgHint"),
                        Margin = new Thickness(0, 0, 0, 6)
                    },
                    FieldLabel(Loc.T("Net.BindTarget")),
                    _targetPicker,
                    FieldLabel(Loc.T("Net.BindResolution")),
                    _resolutionPicker,
                    FieldLabel(Loc.T("Net.BindBitrate")),
                    _bitratePicker,
                    FieldLabel(Loc.T("Net.BindQuality")),
                    _qualityPicker
                }
            }
        };
        _shell.Footer = new HorizontalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = LayoutOptions.End,
            Children = { cancel, ok }
        };

        Content = _shell;
    }

    public Task<RemoteProjectChoice?> WaitAsync() => _tcs.Task;

    private async Task FinishAsync(RemoteProjectChoice? choice)
    {
        _tcs.TrySetResult(choice);
        try { await Navigation.PopModalAsync(); }
        catch { /* ignore */ }
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Style = TryStyle("DlgSectionTitle"),
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static void StyleField(Picker picker)
    {
        picker.HeightRequest = 40;
        if (Application.Current?.Resources.TryGetValue("Text", out var t) == true && t is Color tc)
            picker.TextColor = tc;
        if (Application.Current?.Resources.TryGetValue("Muted", out var m) == true && m is Color mc)
            picker.TitleColor = mc;
    }

    private static Button MakePrimary(string text)
    {
        var b = new Button { Text = text };
        if (TryStyle("DlgPrimaryBtn") is { } s) b.Style = s;
        return b;
    }

    private static Button MakeNormal(string text)
    {
        var b = new Button { Text = text };
        if (TryStyle("DlgNormalBtn") is { } s) b.Style = s;
        return b;
    }

    private static Style? TryStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;

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

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tcs.TrySetResult(null);
    }
}
