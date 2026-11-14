using FlyScreen.Tool.VideoWall.Contracts;
using LibVLCSharp.Shared;
using MediaStats = FlyScreen.Tool.VideoWall.Contracts.MediaStats;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

/// <summary>单路 LibVLC <see cref="MediaPlayer"/> 封装（共享一个 <see cref="LibVLC"/> 实例）。</summary>
public sealed class VlcMediaPlayer : IMediaPlayer, IDisposable
{
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);

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
        _player.Paused += (_, _) => SetState(TilePlayState.Stopped);
        _player.Stopped += (_, _) => SetState(TilePlayState.Stopped);
        _player.EndReached += (_, _) => SetState(TilePlayState.Stopped);
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

    public event Action<TilePlayState>? StateChanged;
    public event Action<MediaStats>? StatsChanged;

    public void SetRenderTarget(IntPtr target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _player.Hwnd = target;
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

    public Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(TilePlayState.Stopping);
        _watchTimer.Stop();
        _player.Stop();
        SetState(TilePlayState.Stopped);
        return Task.CompletedTask;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watchTimer.Stop();
        _watchTimer.Dispose();
        _player.Stop();
        _media?.Dispose();
        _player.Dispose();
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
        var tracks = _player.Media?.Tracks;
        var video = tracks?.FirstOrDefault(t => t.TrackType == TrackType.Video);
        var width = 0;
        var height = 0;
        double fps = 0;
        string? codec = null;

        if (video is not null)
        {
            width = (int)video.Data.Video.Width;
            height = (int)video.Data.Video.Height;
            fps = video.Data.Video.FrameRateNum / (double)Math.Max(1, video.Data.Video.FrameRateDen);
            codec = video.Codec;
        }

        var firstFrameMs = _state is TilePlayState.Playing or TilePlayState.Stalled
            ? (int)Math.Max(0, (DateTime.UtcNow - _playStartUtc).TotalMilliseconds)
            : -1;

        StatsChanged?.Invoke(new MediaStats
        {
            Width = width,
            Height = height,
            Fps = fps,
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