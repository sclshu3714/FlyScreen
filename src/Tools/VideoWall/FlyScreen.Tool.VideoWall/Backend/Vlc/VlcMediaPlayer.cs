using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.Tool.VideoWall.Contracts;
using LibVLCSharp.Shared;
using MediaStats = FlyScreen.Tool.VideoWall.Contracts.MediaStats;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

/// <summary>单路 LibVLC <see cref="MediaPlayer"/> 封装（共享一个 <see cref="LibVLC"/> 实例）。</summary>
public sealed class VlcMediaPlayer : IMediaPlayer, IDisposable
{
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);
    private static readonly byte[] Rv32Chroma = Encoding.ASCII.GetBytes("RV32");

    private readonly LibVLC _libVlc;
    private readonly VideoEndpoint _endpoint;
    private readonly MediaPlayerOptions _options;
    private readonly Sources.CredentialStore? _credentials;
    private readonly MediaPlayer _player;
    private readonly object _gate = new();
    private readonly System.Timers.Timer _watchTimer;

    private Media? _media;
    private TilePlayState _state = TilePlayState.Idle;
    private VideoErrorCode _error = VideoErrorCode.None;
    private long _lastTime = -1;
    private DateTime _lastProgressUtc = DateTime.UtcNow;
    private DateTime _playStartUtc;
    private bool _disposed;
    private volatile bool _loop;

    // 软件帧出画（须在 Play 前绑定；委托字段防 GC）
    private IVideoFrameSink? _frameSink;
    private MediaPlayer.LibVLCVideoFormatCb? _formatCb;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;
    private IntPtr _frameAlloc;
    private IntPtr _frameAligned;
    private int _frameAllocSize;
    private uint _frameWidth;
    private uint _frameHeight;
    private uint _framePitch;

    public VlcMediaPlayer(
        LibVLC libVlc,
        VideoEndpoint endpoint,
        MediaPlayerOptions options,
        Sources.CredentialStore? credentials)
    {
        _libVlc = libVlc;
        _endpoint = endpoint;
        _options = options;
        _credentials = credentials;
        _player = new MediaPlayer(libVlc);

        _player.Opening += (_, _) => SetState(TilePlayState.Connecting);
        _player.Buffering += (_, _) => SetState(TilePlayState.Buffering);
        _player.Playing += OnPlaying;
        _player.Paused += (_, _) => SetState(TilePlayState.Paused);
        _player.Stopped += (_, _) => SetState(TilePlayState.Stopped);
        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnEncounteredError;

        _watchTimer = new System.Timers.Timer(500) { AutoReset = true };
        _watchTimer.Elapsed += (_, _) => OnWatchTick();

        if (options.RenderTarget is IntPtr hwnd && hwnd != IntPtr.Zero)
            _player.Hwnd = hwnd;

        if (options.Mute)
            _player.Mute = true;
    }

    public TilePlayState State
    {
        get { lock (_gate) return _state; }
    }

    public VideoErrorCode ErrorCode
    {
        get { lock (_gate) return _error; }
    }

    /// <summary>供 WPF <c>VideoView.MediaPlayer</c> 绑定；勿再手动设 HWND。</summary>
    public MediaPlayer NativeMediaPlayer => _player;

    public long TimeMs
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var t = _player.Time;
            return t < 0 ? 0 : t;
        }
    }

    public long LengthMs
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var len = _player.Length;
            return len < 0 ? 0 : len;
        }
    }

    public bool CanSeek
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _player.IsSeekable;
        }
    }

    public event Action<TilePlayState>? StateChanged;
    public event Action<MediaStats>? StatsChanged;

    public void SetRenderTarget(IntPtr target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 软件帧模式下忽略 HWND，避免与回调出画冲突
        if (_frameSink is not null)
            return;
        // VideoView 绑定时由其托管 HWND；仅在显式传入有效句柄时覆盖
        if (target != IntPtr.Zero)
            _player.Hwnd = target;
    }

    public void AttachFrameSink(IVideoFrameSink? sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frameSink = sink;
        if (sink is null)
            return;

        // 与 HWND 互斥：清窗口句柄，改走内存回调
        _player.Hwnd = IntPtr.Zero;
        _formatCb = OnVideoFormat;
        _cleanupCb = OnVideoCleanup;
        _lockCb = OnVideoLock;
        _displayCb = OnVideoDisplay;
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        SetState(TilePlayState.Resolving);
        _playStartUtc = DateTime.UtcNow;

        _media?.Dispose();
        _media = VlcMrlHelper.CreateMedia(_libVlc, _endpoint, _options, _credentials);
        _player.Media = _media;

        SetState(TilePlayState.Connecting);
        _player.Play();
        _watchTimer.Start();

        return Task.CompletedTask;
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state is TilePlayState.Playing or TilePlayState.Buffering or TilePlayState.Stalled)
            _player.SetPause(true);
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state == TilePlayState.Paused)
            _player.SetPause(false);
        else if (_state is TilePlayState.Stopped or TilePlayState.Idle)
            _player.Play();
    }

    public Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(TilePlayState.Stopping);
        _watchTimer.Stop();
        _player.Stop();
        SetState(TilePlayState.Stopped);
        return Task.CompletedTask;
    }

    public void SeekTo(long timeMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_player.IsSeekable) return;
        var len = _player.Length;
        var target = timeMs < 0 ? 0 : timeMs;
        if (len > 0)
            target = Math.Min(target, Math.Max(0, len - 1));
        _player.Time = target;
    }

    public void SeekBy(long deltaMs) => SeekTo(TimeMs + deltaMs);

    public void SetVolume(int volume0To100)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _player.Volume = Math.Clamp(volume0To100, 0, 100);
    }

    public void SetMuted(bool muted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _player.Mute = muted;
    }

    public void SetRate(float rate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _player.SetRate(Math.Clamp(rate, 0.25f, 4f));
    }

    public void SetLoop(bool loop) => _loop = loop;

    private void OnEndReached(object? sender, EventArgs e)
    {
        if (!_loop || _disposed)
        {
            SetState(TilePlayState.Stopped);
            return;
        }

        // LibVLC 事件线程内不可直接调 Play/Stop，丢到线程池重播。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (_disposed || !_loop) return;
                _player.Stop();
                _player.Play();
                _player.Time = 0;
            }
            catch { /* 播放器已释放 */ }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watchTimer.Stop();
        _watchTimer.Dispose();
        _player.Stop();
        _media?.Dispose();
        _player.Dispose();
        FreeFrameBuffer();
        _frameSink = null;
    }

    private uint OnVideoFormat(
        ref IntPtr opaque,
        IntPtr chroma,
        ref uint width,
        ref uint height,
        ref uint pitches,
        ref uint lines)
    {
        Marshal.Copy(Rv32Chroma, 0, chroma, 4);

        // 对齐到偶数，避免部分转换器踩奇宽高
        if ((width & 1) != 0) width++;
        if ((height & 1) != 0) height++;

        pitches = width * 4;
        lines = height;
        _frameWidth = width;
        _frameHeight = height;
        _framePitch = pitches;

        var bytes = checked((int)(pitches * lines));
        EnsureFrameBuffer(bytes);
        try
        {
            _frameSink?.OnVideoFormat((int)width, (int)height, (int)pitches);
        }
        catch
        {
            /* 宿主侧异常不影响解码 */
        }

        return 1;
    }

    private void OnVideoCleanup(ref IntPtr opaque) => FreeFrameBuffer();

    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _frameAligned);
        return _frameAligned;
    }

    private void OnVideoDisplay(IntPtr opaque, IntPtr picture)
    {
        var sink = _frameSink;
        if (sink is null || _frameAligned == IntPtr.Zero || _frameWidth == 0 || _frameHeight == 0)
            return;
        try
        {
            sink.OnVideoFrame(_frameAligned, (int)_frameWidth, (int)_frameHeight, (int)_framePitch);
        }
        catch
        {
            /* 宿主侧异常不影响解码 */
        }
    }

    private void EnsureFrameBuffer(int size)
    {
        if (_frameAlloc != IntPtr.Zero && _frameAllocSize >= size)
            return;

        FreeFrameBuffer();
        // +63：Alloc 后对齐到 32 字节边界（LibVLC 要求）
        _frameAlloc = Marshal.AllocHGlobal(size + 63);
        _frameAllocSize = size;
        var addr = _frameAlloc.ToInt64();
        _frameAligned = new IntPtr((addr + 31) & ~31L);
    }

    private void FreeFrameBuffer()
    {
        if (_frameAlloc == IntPtr.Zero) return;
        Marshal.FreeHGlobal(_frameAlloc);
        _frameAlloc = IntPtr.Zero;
        _frameAligned = IntPtr.Zero;
        _frameAllocSize = 0;
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        SetState(TilePlayState.Playing);
        _lastProgressUtc = DateTime.UtcNow;
        _lastTime = _player.Time;
        PublishStats();
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        var msg = _player.Media?.Meta(LibVLCSharp.Shared.MetadataType.Title) ?? "播放错误";
        _error = VlcErrorMapper.FromMessage(msg);
        SetState(TilePlayState.Failed);
    }

    private void OnWatchTick()
    {
        if (_disposed) return;

        TilePlayState state;
        lock (_gate)
            state = _state;

        if (state != TilePlayState.Playing)
            return;

        var time = _player.Time;
        if (time != _lastTime)
        {
            _lastTime = time;
            _lastProgressUtc = DateTime.UtcNow;
            if (_state == TilePlayState.Stalled)
                SetState(TilePlayState.Playing);
        }
        else if (DateTime.UtcNow - _lastProgressUtc > StallThreshold)
        {
            SetState(TilePlayState.Stalled);
        }

        if (DateTime.UtcNow.Second % 2 == 0)
            PublishStats();
    }

    private void PublishStats()
    {
        VlcTrackInfo.TryGetVideo(_player.Media, out var video);

        var firstFrameMs = _state is TilePlayState.Playing or TilePlayState.Stalled
            ? (int)Math.Max(0, (DateTime.UtcNow - _playStartUtc).TotalMilliseconds)
            : -1;

        StatsChanged?.Invoke(new MediaStats
        {
            Width = video.Width,
            Height = video.Height,
            Fps = video.Fps,
            BitrateKbps = 0,
            BufferMs = _options.BufferMs ?? _endpoint.BufferMs ?? 0,
            FirstFrameMs = firstFrameMs,
            HardwareDecode = !(_options.DecodePreference ?? "auto").Equals("software", StringComparison.OrdinalIgnoreCase)
        });
    }

    private void SetState(TilePlayState state)
    {
        bool raise;
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
            raise = true;
        }

        if (raise)
            StateChanged?.Invoke(state);
    }
}