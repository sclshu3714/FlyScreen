using System.IO;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.SharedDevices;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.Tool.SharedDevices.Ui.Maui;

/// <summary>测试对端共享设备：摄像头画面 / 扬声器试音 / 麦克风电平（居中卡片弹层）。</summary>
public sealed class SharedDevicesTestPage : ContentPage
{
    private readonly TaskCompletionSource _closed = new();
    private readonly SharedDevicesService? _sd = ToolService.Instance.GetToolService<SharedDevicesService>();
    private readonly Image _camPreview = new() { Aspect = Aspect.AspectFit, HeightRequest = 200 };
    private readonly Label _camStatus;
    private readonly Label _camHint;
    private readonly Label _speakerHint;
    private readonly Label _speakerStatus;
    private readonly Label _micHint;
    private readonly Label _micStatus;
    private readonly ProgressBar _micLevel = new() { Progress = 0 };
    private readonly Button _camStart;
    private readonly Button _camStop;
    private readonly Button _speakerBtn;
    private readonly Button _micStart;
    private readonly Button _micStop;
    private readonly Button _startSessionBtn;
    private readonly Border _dialogCard;
    private bool _camOn;
    private bool _micOn;
    private bool _speakerBusy;
    private double _micPeak;
    private IDispatcherTimer? _uiTimer;

    public Task Closed => _closed.Task;

    public SharedDevicesTestPage()
    {
        Title = Loc.T("Tool.SharedDevices.TestTitle");
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Color.FromRgba(0, 0, 0, 0.35);

        var text = ColorOf("Text") ?? Colors.Black;
        var muted = ColorOf("Muted") ?? Colors.Gray;
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var card = ColorOf("CardBg") ?? Colors.White;
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");

        _camStatus = new Label
        {
            FontSize = 12,
            TextColor = muted,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        _camHint = new Label { FontSize = 12, TextColor = muted, LineBreakMode = LineBreakMode.WordWrap };
        _speakerHint = new Label { FontSize = 12, TextColor = muted, LineBreakMode = LineBreakMode.WordWrap };
        _speakerStatus = new Label { FontSize = 12, TextColor = muted, VerticalOptions = LayoutOptions.Center };
        _micHint = new Label { FontSize = 12, TextColor = muted, LineBreakMode = LineBreakMode.WordWrap };
        _micStatus = new Label { FontSize = 12, TextColor = muted, VerticalOptions = LayoutOptions.Center };

        _camStart = PrimaryBtn(Loc.T("Tool.SharedDevices.TestStart"), blue);
        _camStop = OutlineBtn(Loc.T("Tool.SharedDevices.TestStop"), text, line);
        _camStop.IsEnabled = false;
        _camStart.Clicked += async (_, _) => await StartCameraAsync();
        _camStop.Clicked += async (_, _) => await StopCameraAsync();

        _speakerBtn = PrimaryBtn(Loc.T("Tool.SharedDevices.TestPlayTone"), blue);
        _speakerBtn.Clicked += async (_, _) => await PlaySpeakerAsync();

        _micStart = PrimaryBtn(Loc.T("Tool.SharedDevices.TestStart"), blue);
        _micStop = OutlineBtn(Loc.T("Tool.SharedDevices.TestStop"), text, line);
        _micStop.IsEnabled = false;
        _micStart.Clicked += async (_, _) => await StartMicAsync();
        _micStop.Clicked += async (_, _) => await StopMicAsync();

        _startSessionBtn = PrimaryBtn(Loc.T("Tool.SharedDevices.TestGoStart"), blue);
        _startSessionBtn.IsVisible = false;
        _startSessionBtn.Clicked += async (_, _) => await EnsureSessionThenRefreshAsync();

        var close = OutlineBtn(Loc.T("Tool.SharedDevices.TestClose"), text, line);
        close.Clicked += async (_, _) => await CloseAsync();

        var body = new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                new Label
                {
                    Text = Loc.T("Tool.SharedDevices.TestTitle"),
                    FontSize = 15,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = text
                },
                _startSessionBtn,
                Section(card, line, Loc.T("Tool.SharedDevices.TestCamera"), _camHint, text,
                    new VerticalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            new HorizontalStackLayout { Spacing = 8, Children = { _camStart, _camStop } },
                            new Border
                            {
                                Stroke = line,
                                StrokeThickness = 1,
                                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                                BackgroundColor = ColorOf("SubtleBg") ?? Color.FromArgb("#F3F4F6"),
                                HeightRequest = 200,
                                Content = new Grid
                                {
                                    Children = { _camPreview, _camStatus }
                                }
                            }
                        }
                    }),
                Section(card, line, Loc.T("Tool.SharedDevices.TestSpeaker"), _speakerHint, text,
                    new HorizontalStackLayout { Spacing = 8, Children = { _speakerBtn, _speakerStatus } }),
                Section(card, line, Loc.T("Tool.SharedDevices.TestMicrophone"), _micHint, text,
                    new VerticalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            new HorizontalStackLayout { Spacing = 8, Children = { _micStart, _micStop, _micStatus } },
                            _micLevel
                        }
                    }),
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    HorizontalOptions = LayoutOptions.End,
                    Children = { close }
                }
            }
        };

        _dialogCard = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            BackgroundColor = card,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Padding = 16,
            Content = new ScrollView
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Default,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
                Content = body
            }
        };

        var host = new Grid
        {
            Padding = new Thickness(16),
            Children = { _dialogCard }
        };
        // 点遮罩不关闭，避免误触中断测试；仅右下角关闭
        Content = host;
        SizeChanged += (_, _) => ApplyCardSize();

        if (_sd != null)
        {
            _sd.CameraJpegReceived += OnCameraJpeg;
            _sd.MicAudioReceived += OnMicPcm;
            _sd.SessionChanged += OnSessionChanged;
        }

        _uiTimer = Dispatcher.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(50);
        _uiTimer.Tick += (_, _) => _micLevel.Progress = Math.Clamp(_micPeak / 100.0, 0, 1);
        _uiTimer.Start();

        RefreshSessionUi();
        ApplyCardSize();
    }

    private void ApplyCardSize()
    {
        var pageW = Width > 1 ? Width : 520;
        var pageH = Height > 1 ? Height : 640;
        var cardW = Math.Min(520, Math.Max(300, pageW - 48));
        var cardH = Math.Min(640, Math.Max(360, pageH - 48));
        _dialogCard.WidthRequest = cardW;
        _dialogCard.MaximumWidthRequest = cardW;
        _dialogCard.MaximumHeightRequest = cardH;
    }

    private async Task CloseAsync()
    {
        await CleanupAsync();
        _closed.TrySetResult();
        if (Navigation.ModalStack.Count > 0)
            await Navigation.PopModalAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await CleanupAsync();
        _closed.TrySetResult();
    }

    private void OnSessionChanged() => MainThread.BeginInvokeOnMainThread(RefreshSessionUi);

    private void RefreshSessionUi()
    {
        var snap = _sd?.CurrentSession;
        var ok = snap is { Phase: SharedDevicesSessionPhase.Active, IsApplicant: true };
        var peer = ok ? snap!.PeerName : "";
        if (!ok)
        {
            _camOn = false;
            _micOn = false;
            _micPeak = 0;
            _camPreview.Source = null;
            _camStop.IsEnabled = false;
            _micStop.IsEnabled = false;
            var need = Loc.T("Tool.SharedDevices.TestNeedSession");
            _camHint.Text = need;
            _speakerHint.Text = need;
            _micHint.Text = need;
            _camStart.IsEnabled = false;
            _speakerBtn.IsEnabled = false;
            _micStart.IsEnabled = false;
            _camStatus.Text = need;
            _camStatus.IsVisible = true;
            _micStatus.Text = "";
            _startSessionBtn.IsVisible = snap is not
                { Phase: SharedDevicesSessionPhase.Requesting, IsApplicant: true };
            if (snap is { Phase: SharedDevicesSessionPhase.Requesting, IsApplicant: true })
            {
                var wait = string.Format(Loc.T("Tool.SharedDevices.TestWaitingAccept"), snap.PeerName);
                _camHint.Text = wait;
                _speakerHint.Text = wait;
                _micHint.Text = wait;
                _camStatus.Text = wait;
            }

            return;
        }

        _startSessionBtn.IsVisible = false;
        var kinds = snap!.Kinds;
        _camHint.Text = string.Format(Loc.T("Tool.SharedDevices.TestRemoteCameraHint"), peer);
        _speakerHint.Text = string.Format(Loc.T("Tool.SharedDevices.TestRemoteSpeakerHint"), peer);
        _micHint.Text = string.Format(Loc.T("Tool.SharedDevices.TestRemoteMicHint"), peer);
        _camStart.IsEnabled = !_camOn && kinds.Any(k =>
            k.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase));
        _speakerBtn.IsEnabled = !_speakerBusy && kinds.Any(k =>
            k.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase));
        _micStart.IsEnabled = !_micOn && kinds.Any(k =>
            k.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase));
        if (!_camOn)
        {
            _camStatus.Text = _camStart.IsEnabled
                ? Loc.T("Tool.SharedDevices.TestCameraIdle")
                : Loc.T("Tool.SharedDevices.TestKindNotInSession");
            _camStatus.IsVisible = true;
        }
    }

    private async Task EnsureSessionThenRefreshAsync()
    {
        if (_sd == null) return;
        if (_sd.CurrentSession is { Phase: SharedDevicesSessionPhase.Active, IsApplicant: true })
        {
            RefreshSessionUi();
            return;
        }

        // 弹出启动页叠在本测试卡之上；完成后刷新
        var page = new SharedDevicesStartPage(_sd.ListApplicantTargets());
        await Navigation.PushModalAsync(page, animated: true);
        var (peer, kinds) = await page.Result;
        if (string.IsNullOrEmpty(peer) || kinds.Length == 0)
        {
            RefreshSessionUi();
            return;
        }

        var (ok, msg) = await _sd.StartRequestAsync(peer!, kinds);
        if (!ok)
        {
            _camStatus.Text = msg;
            _camStatus.IsVisible = true;
        }

        RefreshSessionUi();
    }

    private void OnCameraJpeg(byte[] jpeg)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_camOn) return;
            try
            {
                var copy = jpeg.ToArray();
                _camPreview.Source = ImageSource.FromStream(() => new MemoryStream(copy));
                _camStatus.IsVisible = false;
            }
            catch
            {
                _camStatus.Text = Loc.T("Tool.SharedDevices.TestCameraFail");
                _camStatus.IsVisible = true;
            }
        });
    }

    private void OnMicPcm(byte[] pcm, int sampleRate, int channels)
    {
        if (!_micOn || pcm.Length < 2) return;
        var max = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var s = Math.Abs(BitConverter.ToInt16(pcm, i));
            if (s > max) max = s;
        }

        _micPeak = Math.Min(100, max / 327.67);
    }

    private async Task StartCameraAsync()
    {
        if (_sd == null) return;
        var (ok, msg) = await _sd.RequestPeerTestAsync(SharedDeviceKinds.Camera, start: true);
        if (!ok)
        {
            _camStatus.Text = msg;
            _camStatus.IsVisible = true;
            return;
        }

        _camOn = true;
        _camStart.IsEnabled = false;
        _camStop.IsEnabled = true;
        _camStatus.Text = Loc.T("Tool.SharedDevices.TestWaitingPeer");
        _camStatus.IsVisible = true;
    }

    private async Task StopCameraAsync()
    {
        _camOn = false;
        _camPreview.Source = null;
        _camStop.IsEnabled = false;
        if (_sd != null)
            await _sd.RequestPeerTestAsync(SharedDeviceKinds.Camera, start: false);
        RefreshSessionUi();
    }

    private async Task PlaySpeakerAsync()
    {
        if (_speakerBusy || _sd == null) return;
        _speakerBusy = true;
        _speakerBtn.IsEnabled = false;
        _speakerStatus.Text = Loc.T("Tool.SharedDevices.TestSpeakerPlaying");
        try
        {
            var (ok, msg) = await _sd.RequestPeerTestAsync(SharedDeviceKinds.Speaker, start: true);
            _speakerStatus.Text = ok ? Loc.T("Tool.SharedDevices.TestSpeakerDone") : msg;
            await Task.Delay(800);
            await _sd.RequestPeerTestAsync(SharedDeviceKinds.Speaker, start: false);
        }
        catch (Exception ex)
        {
            _speakerStatus.Text = ex.Message;
        }
        finally
        {
            _speakerBusy = false;
            RefreshSessionUi();
        }
    }

    private async Task StartMicAsync()
    {
        if (_sd == null) return;
        var (ok, msg) = await _sd.RequestPeerTestAsync(SharedDeviceKinds.Microphone, start: true);
        if (!ok)
        {
            _micStatus.Text = msg;
            return;
        }

        _micOn = true;
        _micPeak = 0;
        _micStart.IsEnabled = false;
        _micStop.IsEnabled = true;
        _micStatus.Text = Loc.T("Tool.SharedDevices.TestMicListening");
    }

    private async Task StopMicAsync()
    {
        _micOn = false;
        _micPeak = 0;
        _micLevel.Progress = 0;
        _micStop.IsEnabled = false;
        if (_sd != null)
            await _sd.RequestPeerTestAsync(SharedDeviceKinds.Microphone, start: false);
        RefreshSessionUi();
        _micStatus.Text = "";
    }

    private async Task CleanupAsync()
    {
        _uiTimer?.Stop();
        _uiTimer = null;
        if (_sd != null)
        {
            _sd.CameraJpegReceived -= OnCameraJpeg;
            _sd.MicAudioReceived -= OnMicPcm;
            _sd.SessionChanged -= OnSessionChanged;
            if (_camOn)
                await _sd.RequestPeerTestAsync(SharedDeviceKinds.Camera, start: false);
            if (_micOn)
                await _sd.RequestPeerTestAsync(SharedDeviceKinds.Microphone, start: false);
        }

        _camOn = false;
        _micOn = false;
    }

    private static View Section(Color card, Color line, string title, View hint, Color text, View body)
    {
        return new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = ColorOf("SubtleBg") ?? Color.FromArgb("#F9FAFB"),
            Padding = 12,
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new Label { Text = title, FontAttributes = FontAttributes.Bold, TextColor = text },
                    hint,
                    body
                }
            }
        };
    }

    private static Button PrimaryBtn(string text, Color blue) => new()
    {
        Text = text,
        BackgroundColor = blue,
        TextColor = Colors.White,
        CornerRadius = 8,
        Padding = new Thickness(14, 8)
    };

    private static Button OutlineBtn(string text, Color fg, Color line) => new()
    {
        Text = text,
        BackgroundColor = Colors.Transparent,
        TextColor = fg,
        BorderColor = line,
        BorderWidth = 1,
        CornerRadius = 8,
        Padding = new Thickness(14, 8)
    };

    private static Color? ColorOf(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return null;
    }
}
