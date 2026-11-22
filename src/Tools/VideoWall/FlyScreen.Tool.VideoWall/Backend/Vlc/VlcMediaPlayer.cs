using System.Reflection;
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

    /// <summary>
    /// Android 版 LibVLCSharp 无 <c>MediaPlayer.Hwnd</c>；工具按 net10.0 桌面 API 编译，
    /// 直接赋值会在运行时 MissingMethodException。反射按宿主程序集是否存在该属性调用。
    /// </summary>
    private static readonly Action<MediaPlayer, IntPtr>? SetHwnd = CreateHwndSetter();

    private static Action<MediaPlayer, IntPtr>? CreateHwndSetter()
    {
        var prop = typeof(MediaPlayer).GetProperty("Hwnd", BindingFlags.Instance | BindingFlags.Public);
        if (prop?.SetMethod is null) return null;
        return (player, hwnd) => prop.SetValue(player, hwnd);
    }

    private static void TrySetPlayerHwnd(MediaPlayer player, IntPtr hwnd)
    {
        // Android LibVLC 走 ANativeWindow / 软件帧，无 HWND；即使反射找到桌面属性也不要调用
        if (OperatingSystem.IsAndroid() || SetHwnd is null)
            return;
        SetHwnd(player, hwnd);
    }

    private readonly LibVLC _libVlc;
    private readonly VideoEndpoint _endpoint;
    private VideoEndpoint _activeEndpoint;
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

    // Seek：last-wins 节流 + 乐观时间；须持续贴近后才取消
    private const int SeekCatchupMs = 400;
    private const int SeekCatchupHoldMs = 400;
    private const int SeekPendingMaxMs = 15_000;
    private const int SeekThrottleMs = 200;
    private const int SeekMaxRetries = 2;
    private const int SeekStoppedRecoverMax = 2;
    private long _pendingSeekMs = -1;
    private long _pendingSeekUntilTick;
    private long _pendingSeekNearSinceTick = -1;
    private long _coalesceSeekTarget = -1;
    private long _coalesceSeekDueTick;
    private int _seekRetryLeft;
    private int _seekStoppedRecoverLeft;
    private int _seekFlushScheduled;

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
        _activeEndpoint = endpoint;
        _options = options;
        _credentials = credentials;
        _player = new MediaPlayer(libVlc);

        _player.Opening += (_, _) => SetState(TilePlayState.Connecting);
        // 直播/RTSP 常持续上报 Buffering；已 Playing/Paused 时勿降级，否则 UI 加载层卡死
        _player.Buffering += OnBuffering;
        _player.Playing += OnPlaying;
        _player.Paused += (_, _) => SetState(TilePlayState.Paused);
        _player.Stopped += OnStopped;
        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnEncounteredError;

        _watchTimer = new System.Timers.Timer(500) { AutoReset = true };
        _watchTimer.Elapsed += (_, _) => OnWatchTick();

        if (options.RenderTarget is IntPtr hwnd && hwnd != IntPtr.Zero)
            TrySetPlayerHwnd(_player, hwnd);

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
            var t = ReadRawTimeMs();

            lock (_gate)
            {
                if (_pendingSeekMs < 0)
                    return t;

                var now = Environment.TickCount64;
                var target = _pendingSeekMs;

                // Time 设目标后可能瞬间读到目标又退回旧值；需持续贴近才确认追上
                if (Math.Abs(t - target) <= SeekCatchupMs)
                {
                    if (_pendingSeekNearSinceTick < 0)
                        _pendingSeekNearSinceTick = now;
                    else if (now - _pendingSeekNearSinceTick >= SeekCatchupHoldMs)
                    {
                        ClearPendingSeek_NoLock();
                        return t;
                    }

                    return target;
                }

                _pendingSeekNearSinceTick = -1;

                if (now >= _pendingSeekUntilTick)
                {
                    // 超时仍远离：有限次重提，避免只伪装后弹回
                    if (_seekRetryLeft > 0)
                    {
                        _seekRetryLeft--;
                        _pendingSeekUntilTick = now + SeekPendingMaxMs / 2;
                        _coalesceSeekTarget = target;
                        ScheduleSeekFlush_NoLock(immediate: true);
                        return target;
                    }

                    ClearPendingSeek_NoLock();
                    return t;
                }

                return target;
            }
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
        // VideoView 绑定时由其托管 HWND；仅在显式传入有效句柄时覆盖（Android 无 Hwnd API）
        if (target != IntPtr.Zero)
            TrySetPlayerHwnd(_player, target);
    }

    public void AttachFrameSink(IVideoFrameSink? sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frameSink = sink;
        if (sink is null)
            return;

        // 与 HWND 互斥：清窗口句柄，改走内存回调（Android 无 Hwnd，跳过）
        TrySetPlayerHwnd(_player, IntPtr.Zero);
        _formatCb = OnVideoFormat;
        _cleanupCb = OnVideoCleanup;
        _lockCb = OnVideoLock;
        _displayCb = OnVideoDisplay;
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        SetState(TilePlayState.Resolving);
        _playStartUtc = DateTime.UtcNow;

        try
        {
            _activeEndpoint = await Sources.UgreenShareResolver.PrepareAsync(_endpoint, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            FlyScreen.Services.AppLog.Warn("视频墙：绿联分享解析超时");
            _error = VideoErrorCode.NetTimeout;
            SetState(TilePlayState.Failed);
            return;
        }
        catch (Sources.UgreenShareException ex)
        {
            FlyScreen.Services.AppLog.Warn($"视频墙：{ex.Message}");
            _error = ex.Code;
            SetState(TilePlayState.Failed);
            return;
        }
        catch (Exception ex)
        {
            FlyScreen.Services.AppLog.Warn($"视频墙：绿联分享解析失败 {ex.Message}");
            _error = VideoErrorCode.NetReset;
            SetState(TilePlayState.Failed);
            return;
        }

        if (_disposed)
            return;

        _media?.Dispose();
        _media = VlcMrlHelper.CreateMedia(_libVlc, _activeEndpoint, _options, _credentials);
        _player.Media = _media;

        SetState(TilePlayState.Connecting);
        var ok = _player.Play();
        FlyScreen.Services.AppLog.Info(
            $"视频墙：Play({DescribeEndpoint()}) → {(ok ? "OK" : "false")} sink={_frameSink is not null}");
        _watchTimer.Start();
        // 无帧/无 Playing 时超时失败，避免手机一直转圈
        _ = WatchFirstFrameOrFailAsync();
    }

    private string DescribeEndpoint()
    {
        var addr = _activeEndpoint.Address ?? "";
        if (addr.Contains("shareMediaPlayer", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(addr, UriKind.Absolute, out var uri))
            addr = uri.GetLeftPart(UriPartial.Path);
        if (addr.Length > 96) addr = addr[..96] + "…";
        return $"{_activeEndpoint.Protocol}:{addr}";
    }

    private async Task WatchFirstFrameOrFailAsync()
    {
        try
        {
            await Task.Delay(12_000).ConfigureAwait(false);
            if (_disposed) return;
            TilePlayState cur;
            lock (_gate) cur = _state;
            if (cur is TilePlayState.Playing or TilePlayState.Paused
                or TilePlayState.Stopped or TilePlayState.Stopping or TilePlayState.Failed)
                return;

            // 有时长或 IsPlaying 仍卡住 → 升 Playing 清遮罩；否则判失败
            if (_player.IsPlaying || _player.Length > 0)
            {
                FlyScreen.Services.AppLog.Warn(
                    $"视频墙：12s 无帧但内核在播，强制 Playing（len={_player.Length}）");
                SetState(TilePlayState.Playing);
                return;
            }

            FlyScreen.Services.AppLog.Warn($"视频墙：12s 无首帧，判定失败 state={cur}");
            _error = VideoErrorCode.NetTimeout;
            SetState(TilePlayState.Failed);
        }
        catch
        {
            /* ignore */
        }
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
        lock (_gate)
            ClearPendingSeek_NoLock();
        SetState(TilePlayState.Stopped);
        return Task.CompletedTask;
    }

    public void SeekTo(long timeMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var len = _player.Length;
        // IsSeekable 在缓冲/seek 中会短暂为 false，有时长仍应尝试
        if (len <= 0 && !_player.IsSeekable) return;

        var target = timeMs < 0 ? 0 : timeMs;
        if (len > 0)
            target = Math.Min(target, Math.Max(0, len - 1));

        lock (_gate)
        {
            _pendingSeekMs = target;
            _pendingSeekUntilTick = Environment.TickCount64 + SeekPendingMaxMs;
            _pendingSeekNearSinceTick = -1;
            _coalesceSeekTarget = target;
            _coalesceSeekDueTick = Environment.TickCount64 + SeekThrottleMs;
            _seekRetryLeft = SeekMaxRetries;
            _seekStoppedRecoverLeft = SeekStoppedRecoverMax;
            ScheduleSeekFlush_NoLock(immediate: false);
        }

        // 防止 seek 期间 Time 卡住被误判为 Stalled
        _lastProgressUtc = DateTime.UtcNow;
        _lastTime = -1;
    }

    public void SeekBy(long deltaMs) => SeekTo(TimeMs + deltaMs);

    private long ReadRawTimeMs()
    {
        var t = _player.Time;
        return t < 0 ? 0 : t;
    }

    private void ClearPendingSeek_NoLock()
    {
        _pendingSeekMs = -1;
        _pendingSeekNearSinceTick = -1;
        _coalesceSeekTarget = -1;
        _seekRetryLeft = 0;
    }

    private void ScheduleSeekFlush_NoLock(bool immediate)
    {
        if (Interlocked.CompareExchange(ref _seekFlushScheduled, 1, 0) != 0)
            return;

        var delay = immediate
            ? 0
            : Math.Max(0, (int)(_coalesceSeekDueTick - Environment.TickCount64));

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (delay > 0)
                    Thread.Sleep(delay);
                FlushCoalescedSeek();
            }
            finally
            {
                Interlocked.Exchange(ref _seekFlushScheduled, 0);
                // 睡眠期间又有新目标：再排一次
                lock (_gate)
                {
                    if (_coalesceSeekTarget >= 0 && !_disposed)
                        ScheduleSeekFlush_NoLock(immediate: false);
                }
            }
        });
    }

    private void FlushCoalescedSeek()
    {
        if (_disposed) return;

        long target;
        lock (_gate)
        {
            target = _coalesceSeekTarget;
            if (target < 0) return;
            // last-wins：若还未到 due 且目标可能仍在更新，等下一轮
            if (Environment.TickCount64 < _coalesceSeekDueTick)
            {
                ScheduleSeekFlush_NoLock(immediate: false);
                return;
            }

            _coalesceSeekTarget = -1;
            _pendingSeekMs = target;
            _pendingSeekNearSinceTick = -1;
        }

        ApplySeekTarget(target);
        _lastProgressUtc = DateTime.UtcNow;
        _lastTime = -1;
    }

    private void ApplySeekTarget(long targetMs)
    {
        try
        {
            // 勿用 Position：部分源设 Position 后会停住无法继续播
            _player.Time = targetMs;
        }
        catch
        {
            /* 播放器可能正在切换媒体 */
        }
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        long pending;
        int recoverLeft;
        lock (_gate)
        {
            pending = _pendingSeekMs;
            recoverLeft = _seekStoppedRecoverLeft;
            if (pending >= 0 && recoverLeft > 0)
                _seekStoppedRecoverLeft = recoverLeft - 1;
        }

        // pending seek 中偶发 Stopped：有限次恢复，避免整段会话挂死
        if (pending >= 0 && recoverLeft > 0 && !_disposed)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (_disposed) return;
                    long keep;
                    lock (_gate) keep = _pendingSeekMs;
                    if (keep < 0) return;

                    if (!_player.IsPlaying)
                        _player.Play();
                    ApplySeekTarget(keep);
                    _lastProgressUtc = DateTime.UtcNow;
                    _lastTime = -1;
                    if (_state is TilePlayState.Stopped or TilePlayState.Idle)
                        SetState(TilePlayState.Buffering);
                }
                catch
                {
                    /* ignore */
                }
            });
            return;
        }

        SetState(TilePlayState.Stopped);
    }

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

        // Android 软件帧走 CPU 拷贝+Bitmap：全高清每帧 ~8MB，UI 跟不上就只剩偶发一帧。
        // 在 format 回调里缩小输出，让 LibVLC 负责缩放。
        if (OperatingSystem.IsAndroid() && width > 960)
        {
            var scaledH = (uint)Math.Max(2, Math.Round(height * (960.0 / width)));
            width = 960;
            height = scaledH;
        }

        // 对齐到偶数，避免部分转换器踩奇宽高
        if ((width & 1) != 0) width++;
        if ((height & 1) != 0) height++;

        // LibVLC 要求 pitch/lines 为 32 对齐，否则 Android 上回调常不出帧
        pitches = Align32(width * 4);
        lines = Align32(height);
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
            // 直播源常只报 Buffering、不二次 Playing；有帧即升为 Playing，UI 才能收加载层
            PromotePlayingFromFrame();
        }
        catch
        {
            /* 宿主侧异常不影响解码 */
        }
    }

    private void PromotePlayingFromFrame()
    {
        TilePlayState cur;
        lock (_gate)
            cur = _state;
        if (cur is not (TilePlayState.Resolving or TilePlayState.Connecting
            or TilePlayState.Buffering or TilePlayState.Reconnecting))
            return;
        SetState(TilePlayState.Playing);
        _lastProgressUtc = DateTime.UtcNow;
    }

    private static uint Align32(uint value)
        => value == 0 ? 0 : (value + 31u) & ~31u;

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

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        lock (_gate)
        {
            if (_state is TilePlayState.Playing or TilePlayState.Paused
                or TilePlayState.Stopping or TilePlayState.Stopped or TilePlayState.Failed)
                return;
        }

        // 缓存打满且内核已在播 → 升为 Playing（部分源不二次发 Playing）
        if (e.Cache >= 99.5f && _player.IsPlaying)
        {
            SetState(TilePlayState.Playing);
            _lastProgressUtc = DateTime.UtcNow;
            return;
        }

        if (_player.IsPlaying)
        {
            SetState(TilePlayState.Playing);
            return;
        }

        SetState(TilePlayState.Buffering);
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
        long pending;
        lock (_gate)
        {
            state = _state;
            pending = _pendingSeekMs;
        }

        if (state != TilePlayState.Playing)
            return;

        // Seek 过程中时钟常停滞，不算卡顿
        if (pending >= 0)
        {
            _lastProgressUtc = DateTime.UtcNow;
            return;
        }

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