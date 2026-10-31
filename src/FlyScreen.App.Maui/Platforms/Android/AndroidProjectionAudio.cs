#if ANDROID
using Android.Content;
using Android.Database;
using Android.Media;
using Android.OS;
using Android.Provider;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>Android 10+：录制正在播放的声音并压低本机媒体音量；收到对端 PCM 时用 AudioTrack 播放。</summary>
public sealed class AndroidProjectionAudio : IProjectionAudioEngine
{
    private static AndroidProjectionAudio? _current;
    private readonly object _gate = new();
    private AudioRecord? _record;
    private AudioTrack? _track;
    private Thread? _thread;
    private volatile bool _run;
    private int _savedMusic = -1;
    private int _playRate;
    private int _playChannels;
    private int _applyingDuck;
    private int _readTick;
    private volatile bool _holdDuck;
    private MusicVolumeObserver? _volumeObserver;
    private VolumeChangedReceiver? _volumeRx;

    public static void StopAny() => _current?.StopCapture();

    public bool TryStartCapture(Action<byte[], int, int> onPcm, out string? error)
    {
        StopCapture();
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            error = "需要 Android 10 或更高版本才能同步声音";
            return false;
        }

        var projection = FlyScreenMediaProjectionService.Instance?.Projection;
        if (projection == null)
        {
            error = "屏幕采集尚未就绪";
            return false;
        }

        try
        {
            var config = new AudioPlaybackCaptureConfiguration.Builder(projection)
                .AddMatchingUsage(AudioUsageKind.Media)!
                .AddMatchingUsage(AudioUsageKind.Game)!
                .AddMatchingUsage(AudioUsageKind.Unknown)!
                .Build();
            var format = new AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(48000)!
                .SetChannelMask(ChannelOut.Stereo)!
                .Build();
            var min = AudioRecord.GetMinBufferSize(48000, ChannelIn.Stereo, Encoding.Pcm16bit);
            _record = new AudioRecord.Builder()
                .SetAudioPlaybackCaptureConfig(config)!
                .SetAudioFormat(format)!
                .SetBufferSizeInBytes(Math.Max(min, 8192))!
                .Build();
            if (_record.State != State.Initialized)
            {
                error = "无法开始录制播放中的声音";
                _record.Release();
                _record = null;
                return false;
            }

            DuckMusic();
            _record.StartRecording();
            _run = true;
            _current = this;
            var rec = _record;
            _thread = new Thread(() => ReadLoop(rec, onPcm)) { IsBackground = true, Name = "FlyScreen.ProjAudio" };
            _thread.Start();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            StopCapture();
            return false;
        }
    }

    public void StopCapture()
    {
        _run = false;
        try { _record?.Stop(); } catch { /* ignore */ }
        try { _thread?.Join(800); } catch { /* ignore */ }
        try { _record?.Release(); } catch { /* ignore */ }
        _record = null;
        _thread = null;
        RestoreMusic();
        if (ReferenceEquals(_current, this)) _current = null;
    }

    public bool TryStartPlayback(out string? error)
    {
        error = null;
        return true;
    }

    public void SubmitPlayback(byte[] pcm, int sampleRate, int channels)
    {
        if (pcm.Length == 0 || sampleRate <= 0 || channels is < 1 or > 2) return;
        lock (_gate)
        {
            if (_track == null || _playRate != sampleRate || _playChannels != channels)
            {
                CloseTrack();
                var mask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
                var min = AudioTrack.GetMinBufferSize(sampleRate, mask, Encoding.Pcm16bit);
                _track = new AudioTrack.Builder()
                    .SetAudioAttributes(new AudioAttributes.Builder()
                        .SetUsage(AudioUsageKind.Media)!
                        .SetContentType(AudioContentType.Music)!
                        .Build())!
                    .SetAudioFormat(new AudioFormat.Builder()
                        .SetEncoding(Encoding.Pcm16bit)!
                        .SetSampleRate(sampleRate)!
                        .SetChannelMask(mask)!
                        .Build())!
                    .SetBufferSizeInBytes(Math.Max(min, pcm.Length * 2))!
                    .SetTransferMode(AudioTrackMode.Stream)!
                    .Build();
                _track.Play();
                _playRate = sampleRate;
                _playChannels = channels;
            }

            try { _track.Write(pcm, 0, pcm.Length); }
            catch { CloseTrack(); }
        }
    }

    public void StopPlayback()
    {
        lock (_gate) CloseTrack();
    }

    private void CloseTrack()
    {
        try { _track?.Stop(); } catch { /* ignore */ }
        try { _track?.Release(); } catch { /* ignore */ }
        _track = null;
        _playRate = 0;
        _playChannels = 0;
    }

    private void ReadLoop(AudioRecord record, Action<byte[], int, int> onPcm)
    {
        var buf = new byte[3840];
        while (_run)
        {
            int n;
            try { n = record.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) { Thread.Sleep(10); continue; }
            if ((++_readTick & 7) == 0) KeepMusicSilent();
            var copy = new byte[n];
            Buffer.BlockCopy(buf, 0, copy, 0, n);
            try { onPcm(copy, 48000, 2); } catch { /* ignore */ }
        }
    }

    private void DuckMusic()
    {
        _holdDuck = true;
        WatchVolume();
        ForceMusicZero(remember: true);
    }

    private void RestoreMusic()
    {
        _holdDuck = false;
        UnwatchVolume();
        if (_savedMusic < 0) return;
        var saved = _savedMusic;
        _savedMusic = -1;
        try
        {
            Audio()?.SetStreamVolume(global::Android.Media.Stream.Music, saved, VolumeNotificationFlags.RemoveSoundAndVibrate);
        }
        catch { /* ignore */ }
    }

    /// <summary>音量键会把媒体音量从 0 抬回去，投影期间立刻压回 0，本机不再出声。</summary>
    private void KeepMusicSilent()
    {
        if (!_holdDuck) return;
        ForceMusicZero(remember: false);
    }

    private void ForceMusicZero(bool remember)
    {
        if (Interlocked.Exchange(ref _applyingDuck, 1) == 1) return;
        try
        {
            var am = Audio();
            if (am == null) return;
            var cur = am.GetStreamVolume(global::Android.Media.Stream.Music);
            if (remember && cur > 0) _savedMusic = cur;
            if (!_holdDuck || cur == 0) return;
            am.SetStreamVolume(global::Android.Media.Stream.Music, 0, VolumeNotificationFlags.RemoveSoundAndVibrate);
        }
        catch { /* ignore */ }
        finally
        {
            Interlocked.Exchange(ref _applyingDuck, 0);
        }
    }

    private void WatchVolume()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var resolver = ctx?.ContentResolver;
            if (ctx == null || resolver == null) return;
            try
            {
                _volumeObserver = new MusicVolumeObserver(this, new Handler(Looper.MainLooper!));
                resolver.RegisterContentObserver(Settings.System.ContentUri!, true, _volumeObserver);
            }
            catch
            {
                _volumeObserver = null;
            }

            try
            {
                _volumeRx = new VolumeChangedReceiver(this);
                var filter = new IntentFilter("android.media.VOLUME_CHANGED_ACTION");
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    ctx.RegisterReceiver(_volumeRx, filter, ReceiverFlags.NotExported);
                else
                    ctx.RegisterReceiver(_volumeRx, filter);
            }
            catch
            {
                _volumeRx = null;
            }
        }
        catch
        {
            UnwatchVolume();
        }
    }

    private void UnwatchVolume()
    {
        var ctx = global::Android.App.Application.Context;
        try
        {
            if (_volumeObserver != null)
                ctx?.ContentResolver?.UnregisterContentObserver(_volumeObserver);
        }
        catch { /* ignore */ }
        _volumeObserver = null;
        try
        {
            if (_volumeRx != null) ctx?.UnregisterReceiver(_volumeRx);
        }
        catch { /* ignore */ }
        _volumeRx = null;
    }

    private static AudioManager? Audio()
        => (AudioManager?)global::Android.App.Application.Context
            .GetSystemService(global::Android.Content.Context.AudioService);

    private sealed class MusicVolumeObserver : ContentObserver
    {
        private readonly AndroidProjectionAudio _owner;
        public MusicVolumeObserver(AndroidProjectionAudio owner, Handler handler) : base(handler) => _owner = owner;
        public override void OnChange(bool selfChange) => _owner.KeepMusicSilent();
        public override void OnChange(bool selfChange, global::Android.Net.Uri? uri) => _owner.KeepMusicSilent();
    }

    private sealed class VolumeChangedReceiver : BroadcastReceiver
    {
        private readonly AndroidProjectionAudio _owner;
        public VolumeChangedReceiver(AndroidProjectionAudio owner) => _owner = owner;
        public override void OnReceive(Context? context, Intent? intent) => _owner.KeepMusicSilent();
    }
}
#endif
