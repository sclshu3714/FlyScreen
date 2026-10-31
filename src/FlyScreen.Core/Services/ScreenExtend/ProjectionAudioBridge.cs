using FlyScreen.Services;

namespace FlyScreen.Services.ScreenExtend;

/// <summary>投影声音：主机采集并静音，从机播放。各端提供 <see cref="IProjectionAudioEngine"/>。</summary>
public interface IProjectionAudioEngine
{
    /// <summary>采集本机正在播放的声音，并让本机扬声器不再播出。PCM 为 s16le。</summary>
    bool TryStartCapture(Action<byte[], int, int> onPcm, out string? error);

    void StopCapture();

    bool TryStartPlayback(out string? error);

    void SubmitPlayback(byte[] pcm, int sampleRate, int channels);

    void StopPlayback();
}

/// <summary>把扩展屏会话和本机声音引擎接在一起。声音同步关闭时不采集、不播放。</summary>
public sealed class ProjectionAudioBridge : IDisposable
{
    private readonly ScreenExtendService _session;
    private readonly IProjectionAudioEngine _engine;
    private int _disposed;

    public ProjectionAudioBridge(ScreenExtendService session, IProjectionAudioEngine engine)
    {
        _session = session;
        _engine = engine;
        _session.HostSessionReady += OnHostReady;
        _session.SinkSessionReady += OnSinkReady;
        _session.SessionEnded += OnSessionEnded;
        _session.AudioReceived += OnAudio;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _session.HostSessionReady -= OnHostReady;
        _session.SinkSessionReady -= OnSinkReady;
        _session.SessionEnded -= OnSessionEnded;
        _session.AudioReceived -= OnAudio;
        StopBoth();
    }

    private void OnHostReady()
    {
        if (!_session.AudioSync) return;
        try
        {
            if (!_engine.TryStartCapture((pcm, rate, ch) => _session.PostAudio(pcm, rate, ch), out var error))
                AppLog.Warn("投影声音采集未启动" + (string.IsNullOrEmpty(error) ? "" : "：" + error));
            else
                AppLog.Info("投影声音：开始把本机声音发往对端");
        }
        catch (Exception ex)
        {
            AppLog.Warn("投影声音采集失败：" + ex.Message);
        }
    }

    private void OnSinkReady()
    {
        if (!_session.AudioSync) return;
        try
        {
            if (!_engine.TryStartPlayback(out var error))
                AppLog.Warn("投影声音播放未启动" + (string.IsNullOrEmpty(error) ? "" : "：" + error));
            else
                AppLog.Info("投影声音：正在播放对端传来的声音");
        }
        catch (Exception ex)
        {
            AppLog.Warn("投影声音播放失败：" + ex.Message);
        }
    }

    private void OnAudio(byte[] pcm, int sampleRate, int channels)
    {
        if (!_session.AudioSync) return;
        try { _engine.SubmitPlayback(pcm, sampleRate, channels); }
        catch { /* ignore */ }
    }

    private void OnSessionEnded() => StopBoth();

    private void StopBoth()
    {
        try { _engine.StopCapture(); } catch { /* ignore */ }
        try { _engine.StopPlayback(); } catch { /* ignore */ }
    }
}
