using FlyScreen.Services;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Tool.SharedDevices;

/// <summary>
/// 共享扬声器最小通路：申请端系统回环采集 → ToolData PCM → 接收端播放。
/// 尚未注册虚拟声卡；本机默认输出有声音时才会传到对端。
/// </summary>
public sealed class SharedDevicesAudioBridge : IDisposable
{
    private readonly SharedDevicesService _session;
    private readonly IProjectionAudioEngine _engine;
    private int _disposed;
    private string? _activePeer;
    private bool _capturing;
    private bool _playing;

    public SharedDevicesAudioBridge(SharedDevicesService session, IProjectionAudioEngine engine)
    {
        _session = session;
        _engine = engine;
        _session.SessionChanged += OnSessionChanged;
        _session.AudioReceived += OnAudio;
        OnSessionChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _session.SessionChanged -= OnSessionChanged;
        _session.AudioReceived -= OnAudio;
        StopBoth();
    }

    private void OnSessionChanged()
    {
        var snap = _session.CurrentSession;
        var speaker = snap is { Phase: SharedDevicesSessionPhase.Active }
                      && snap.Kinds.Any(k => k.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase));
        if (!speaker || snap == null)
        {
            StopBoth();
            _activePeer = null;
            return;
        }

        _activePeer = snap.PeerName;
        if (snap.IsApplicant)
        {
            // 申请端：采集本机系统声音发往提供方扬声器
            StopPlaybackOnly();
            if (_capturing) return;
            try
            {
                if (!_engine.TryStartCapture((pcm, rate, ch) => _session.PostAudio(pcm, rate, ch), out var error))
                {
                    AppLog.Warn("共享扬声器采集未启动" + (string.IsNullOrEmpty(error) ? "" : "：" + error));
                    _session.PublishStatus("共享扬声器：无法采集本机声音" +
                                           (string.IsNullOrEmpty(error) ? "" : "（" + error + "）"));
                    return;
                }

                _capturing = true;
                _session.PublishStatus("共享扬声器：正在把本机系统声音发到「" + snap.PeerName +
                                       "」（暂未注册虚拟声卡，有系统出声时才会传输）");
                AppLog.Info("共享扬声器：申请端开始采集");
            }
            catch (Exception ex)
            {
                AppLog.Warn("共享扬声器采集失败：" + ex.Message);
            }
        }
        else
        {
            // 接收端：播放对端发来的 PCM
            StopCaptureOnly();
            if (_playing) return;
            try
            {
                if (!_engine.TryStartPlayback(out var error))
                {
                    AppLog.Warn("共享扬声器播放未启动" + (string.IsNullOrEmpty(error) ? "" : "：" + error));
                    return;
                }

                _playing = true;
                _session.PublishStatus("共享扬声器：正在播放「" + snap.PeerName + "」发来的声音");
                AppLog.Info("共享扬声器：接收端开始播放");
            }
            catch (Exception ex)
            {
                AppLog.Warn("共享扬声器播放失败：" + ex.Message);
            }
        }
    }

    private void OnAudio(byte[] pcm, int sampleRate, int channels)
    {
        if (!_playing) return;
        try { _engine.SubmitPlayback(pcm, sampleRate, channels); }
        catch { /* ignore */ }
    }

    private void StopBoth()
    {
        StopCaptureOnly();
        StopPlaybackOnly();
    }

    private void StopCaptureOnly()
    {
        if (!_capturing) return;
        _capturing = false;
        try { _engine.StopCapture(); } catch { /* ignore */ }
    }

    private void StopPlaybackOnly()
    {
        if (!_playing) return;
        _playing = false;
        try { _engine.StopPlayback(); } catch { /* ignore */ }
    }
}
