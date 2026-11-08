using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Services.Tools.SharedDevices;

namespace FlyScreen.DesktopUi;

/// <summary>测试对端共享设备：摄像头画面 / 扬声器试音 / 麦克风电平。</summary>
public partial class SharedDevicesTestWindow : Window
{
    private readonly SharedDevicesService? _sd = ToolService.Instance.SharedDevices;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private double _micPeak;
    private bool _camOn;
    private bool _micOn;
    private bool _speakerBusy;

    public SharedDevicesTestWindow()
    {
        InitializeComponent();
        Title = Loc.T("Tool.SharedDevices.TestTitle");
        TitleText.Text = Loc.T("Tool.SharedDevices.TestTitle");
        CameraTitle.Text = Loc.T("Tool.SharedDevices.TestCamera");
        CameraHint.Text = Loc.T("Tool.SharedDevices.TestRemoteCameraHint");
        CameraStartBtn.Content = Loc.T("Tool.SharedDevices.TestStart");
        CameraStopBtn.Content = Loc.T("Tool.SharedDevices.TestStop");
        CameraStatus.Text = Loc.T("Tool.SharedDevices.TestCameraIdle");
        SpeakerTitle.Text = Loc.T("Tool.SharedDevices.TestSpeaker");
        SpeakerHint.Text = Loc.T("Tool.SharedDevices.TestRemoteSpeakerHint");
        SpeakerPlayBtn.Content = Loc.T("Tool.SharedDevices.TestPlayTone");
        MicTitle.Text = Loc.T("Tool.SharedDevices.TestMicrophone");
        MicHint.Text = Loc.T("Tool.SharedDevices.TestRemoteMicHint");
        MicStartBtn.Content = Loc.T("Tool.SharedDevices.TestStart");
        MicStopBtn.Content = Loc.T("Tool.SharedDevices.TestStop");
        CloseBtn.Content = Loc.T("Tool.SharedDevices.TestClose");

        RefreshSessionUi();
        if (_sd != null)
        {
            _sd.CameraJpegReceived += OnCameraJpeg;
            _sd.MicAudioReceived += OnMicPcm;
            _sd.SessionChanged += OnSessionChanged;
        }

        _uiTimer.Tick += (_, _) => MicLevel.Value = Math.Clamp(_micPeak, 0, 100);
        _uiTimer.Start();
        Closed += (_, _) => Cleanup();
    }

    private void RefreshSessionUi()
    {
        var snap = _sd?.CurrentSession;
        var ok = snap is { Phase: SharedDevicesSessionPhase.Active, IsApplicant: true };
        var peer = ok ? snap!.PeerName : "";
        if (!ok)
        {
            CameraHint.Text = Loc.T("Tool.SharedDevices.TestNeedSession");
            SpeakerHint.Text = Loc.T("Tool.SharedDevices.TestNeedSession");
            MicHint.Text = Loc.T("Tool.SharedDevices.TestNeedSession");
            CameraStartBtn.IsEnabled = false;
            SpeakerPlayBtn.IsEnabled = false;
            MicStartBtn.IsEnabled = false;
            CameraStatus.Text = Loc.T("Tool.SharedDevices.TestNeedSession");
            return;
        }

        CameraHint.Text = string.Format(Loc.T("Tool.SharedDevices.TestRemoteCameraHint"), peer);
        SpeakerHint.Text = string.Format(Loc.T("Tool.SharedDevices.TestRemoteSpeakerHint"), peer);
        MicHint.Text = string.Format(Loc.T("Tool.SharedDevices.TestRemoteMicHint"), peer);
        var kinds = snap!.Kinds;
        CameraStartBtn.IsEnabled = !_camOn && kinds.Any(k =>
            k.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase));
        SpeakerPlayBtn.IsEnabled = !_speakerBusy && kinds.Any(k =>
            k.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase));
        MicStartBtn.IsEnabled = !_micOn && kinds.Any(k =>
            k.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase));
        if (!_camOn)
        {
            CameraStatus.Text = CameraStartBtn.IsEnabled
                ? Loc.T("Tool.SharedDevices.TestCameraIdle")
                : Loc.T("Tool.SharedDevices.TestKindNotInSession");
            CameraStatus.IsVisible = true;
        }
    }

    private void OnSessionChanged() => Dispatcher.UIThread.Post(RefreshSessionUi);

    private void OnCameraJpeg(byte[] jpeg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_camOn) return;
            try
            {
                using var ms = new MemoryStream(jpeg);
                CameraPreview.Source = new Bitmap(ms);
                CameraStatus.IsVisible = false;
            }
            catch
            {
                CameraStatus.Text = Loc.T("Tool.SharedDevices.TestCameraFail");
                CameraStatus.IsVisible = true;
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

    private async void CameraStart_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sd == null) return;
        var (ok, msg) = await _sd.RequestPeerTestAsync(SharedDeviceKinds.Camera, start: true);
        if (!ok)
        {
            CameraStatus.Text = msg;
            CameraStatus.IsVisible = true;
            return;
        }

        _camOn = true;
        CameraStartBtn.IsEnabled = false;
        CameraStopBtn.IsEnabled = true;
        CameraStatus.Text = Loc.T("Tool.SharedDevices.TestWaitingPeer");
        CameraStatus.IsVisible = true;
    }

    private async void CameraStop_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _camOn = false;
        CameraPreview.Source = null;
        CameraStopBtn.IsEnabled = false;
        if (_sd != null)
            await _sd.RequestPeerTestAsync(SharedDeviceKinds.Camera, start: false);
        RefreshSessionUi();
    }

    private async void SpeakerPlay_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_speakerBusy || _sd == null) return;
        _speakerBusy = true;
        SpeakerPlayBtn.IsEnabled = false;
        SpeakerStatus.Text = Loc.T("Tool.SharedDevices.TestSpeakerPlaying");
        try
        {
            var (ok, msg) = await _sd.RequestPeerTestAsync(SharedDeviceKinds.Speaker, start: true);
            SpeakerStatus.Text = ok ? Loc.T("Tool.SharedDevices.TestSpeakerDone") : msg;
            await Task.Delay(800);
            await _sd.RequestPeerTestAsync(SharedDeviceKinds.Speaker, start: false);
        }
        catch (Exception ex)
        {
            SpeakerStatus.Text = ex.Message;
        }
        finally
        {
            _speakerBusy = false;
            RefreshSessionUi();
        }
    }

    private async void MicStart_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sd == null) return;
        var (ok, msg) = await _sd.RequestPeerTestAsync(SharedDeviceKinds.Microphone, start: true);
        if (!ok)
        {
            MicStatus.Text = msg;
            return;
        }

        _micOn = true;
        _micPeak = 0;
        MicStartBtn.IsEnabled = false;
        MicStopBtn.IsEnabled = true;
        MicStatus.Text = Loc.T("Tool.SharedDevices.TestMicListening");
    }

    private async void MicStop_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _micOn = false;
        _micPeak = 0;
        MicLevel.Value = 0;
        MicStopBtn.IsEnabled = false;
        if (_sd != null)
            await _sd.RequestPeerTestAsync(SharedDeviceKinds.Microphone, start: false);
        RefreshSessionUi();
        MicStatus.Text = "";
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void Cleanup()
    {
        _uiTimer.Stop();
        if (_sd != null)
        {
            _sd.CameraJpegReceived -= OnCameraJpeg;
            _sd.MicAudioReceived -= OnMicPcm;
            _sd.SessionChanged -= OnSessionChanged;
            if (_camOn)
                _ = _sd.RequestPeerTestAsync(SharedDeviceKinds.Camera, start: false);
            if (_micOn)
                _ = _sd.RequestPeerTestAsync(SharedDeviceKinds.Microphone, start: false);
        }

        _camOn = false;
        _micOn = false;
    }
}
