using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.SharedDevices;

namespace FlyScreen.Tool.SharedDevices.Ui.Win;

/// <summary>测试对端（如手机）共享设备：摄像头画面 / 扬声器试音 / 麦克风电平。</summary>
public partial class SharedDevicesTestDialog : Window
{
    private readonly SharedDevicesService? _sd = ToolService.Instance.GetToolService<SharedDevicesService>();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private double _micPeak;
    private bool _camOn;
    private bool _micOn;
    private bool _speakerBusy;

    public SharedDevicesTestDialog()
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

        CameraCombo.Visibility = Visibility.Collapsed;
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
        CameraStartBtn.IsEnabled = snap!.Kinds.Any(k =>
            k.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase));
        SpeakerPlayBtn.IsEnabled = snap.Kinds.Any(k =>
            k.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase));
        MicStartBtn.IsEnabled = snap.Kinds.Any(k =>
            k.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase));
        CameraStatus.Text = CameraStartBtn.IsEnabled
            ? Loc.T("Tool.SharedDevices.TestCameraIdle")
            : Loc.T("Tool.SharedDevices.TestKindNotInSession");
    }

    private void OnSessionChanged() => Dispatcher.BeginInvoke(RefreshSessionUi);

    private void OnCameraJpeg(byte[] jpeg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_camOn) return;
            try
            {
                using var ms = new MemoryStream(jpeg);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                CameraPreview.Source = bmp;
                CameraStatus.Visibility = Visibility.Collapsed;
            }
            catch
            {
                CameraStatus.Visibility = Visibility.Visible;
                CameraStatus.Text = Loc.T("Tool.SharedDevices.TestCameraFail");
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

    private async void CameraStart_Click(object sender, RoutedEventArgs e)
    {
        if (_sd == null) return;
        var (ok, msg) = await _sd.RequestPeerTestAsync(SharedDeviceKinds.Camera, start: true);
        if (!ok)
        {
            CameraStatus.Visibility = Visibility.Visible;
            CameraStatus.Text = msg;
            return;
        }

        _camOn = true;
        CameraStartBtn.IsEnabled = false;
        CameraStopBtn.IsEnabled = true;
        CameraStatus.Visibility = Visibility.Visible;
        CameraStatus.Text = Loc.T("Tool.SharedDevices.TestWaitingPeer");
    }

    private async void CameraStop_Click(object sender, RoutedEventArgs e)
    {
        _camOn = false;
        CameraPreview.Source = null;
        CameraStopBtn.IsEnabled = false;
        if (_sd != null)
            await _sd.RequestPeerTestAsync(SharedDeviceKinds.Camera, start: false);
        RefreshSessionUi();
        CameraStatus.Visibility = Visibility.Visible;
        CameraStatus.Text = Loc.T("Tool.SharedDevices.TestCameraIdle");
    }

    private async void SpeakerPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_speakerBusy || _sd == null) return;
        _speakerBusy = true;
        SpeakerPlayBtn.IsEnabled = false;
        SpeakerStatus.Text = Loc.T("Tool.SharedDevices.TestSpeakerPlaying");
        try
        {
            // 让手机本机播放试音（对端扬声器）
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

    private async void MicStart_Click(object sender, RoutedEventArgs e)
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

    private async void MicStop_Click(object sender, RoutedEventArgs e)
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e) => Cleanup();

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

