#if ANDROID
using Android.Graphics;
using Android.Hardware;
using Android.Media;
using Android.OS;
using Android.Views;
using FlyScreen.ToolSdk;
using Camera = Android.Hardware.Camera;
using ARect = Android.Graphics.Rect;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>提供方本机测试：摄像头 JPEG 上行、麦克风 PCM 上行、扬声器试音。</summary>
public sealed class AndroidSharedDevicesProviderMedia : IDisposable
{
    private readonly ISharedDevicesRuntime _svc;
    private readonly object _gate = new();
    private Camera? _camera;
    private SurfaceTexture? _previewTex;
    private volatile bool _camRun;
    private long _lastJpegMs;
    private AudioRecord? _mic;
    private Thread? _micThread;
    private volatile bool _micRun;
    private AudioTrack? _toneTrack;
    private int _disposed;

    public AndroidSharedDevicesProviderMedia(ISharedDevicesRuntime svc)
    {
        _svc = svc;
        _svc.ProviderTestCommand += OnTestCommand;
        _svc.SessionChanged += OnSessionChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _svc.ProviderTestCommand -= OnTestCommand;
        _svc.SessionChanged -= OnSessionChanged;
        StopAll();
    }

    private void OnSessionChanged()
    {
        if (!_svc.IsProviderSessionActive)
            StopAll();
    }

    private void OnTestCommand(string kind, bool start)
    {
        if (!start)
        {
            StopKind(kind);
            return;
        }

        _ = StartKindAsync(kind);
    }

    private async Task StartKindAsync(string kind)
    {
        try
        {
            if (kind.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase))
            {
                var perm = await MainThread.InvokeOnMainThreadAsync(async () =>
                    await Permissions.RequestAsync<Permissions.Camera>());
                if (perm != PermissionStatus.Granted)
                {
                    _svc.PublishStatus("未授予摄像头权限");
                    return;
                }

                await MainThread.InvokeOnMainThreadAsync(StartCamera);
            }
            else if (kind.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase))
            {
                var perm = await MainThread.InvokeOnMainThreadAsync(async () =>
                    await Permissions.RequestAsync<Permissions.Microphone>());
                if (perm != PermissionStatus.Granted)
                {
                    _svc.PublishStatus("未授予麦克风权限");
                    return;
                }

                await Task.Run(StartMic);
            }
            else if (kind.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase))
            {
                await PlayToneAsync();
            }
        }
        catch (Exception ex)
        {
            _svc.PublishStatus("本机测试失败：" + ex.Message);
        }
    }

    private void StopKind(string kind)
    {
        if (kind.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase))
            StopCamera();
        else if (kind.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase))
            StopMic();
        else if (kind.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase))
            StopTone();
    }

    private void StopAll()
    {
        StopCamera();
        StopMic();
        StopTone();
    }

    private void StartCamera()
    {
        lock (_gate)
        {
            StopCameraUnlocked();
            var n = Camera.NumberOfCameras;
            if (n <= 0)
            {
                _svc.PublishStatus("本机没有摄像头");
                return;
            }

            var index = 0;
            var selected = _svc.GetSelectedDeviceId(SharedDeviceKinds.Camera);
            if (!string.IsNullOrEmpty(selected) && int.TryParse(selected, out var sel) && sel >= 0 && sel < n)
                index = sel;
            else
            {
                var info = new Camera.CameraInfo();
                for (var i = 0; i < n; i++)
                {
                    Camera.GetCameraInfo(i, info);
                    if (info.Facing == CameraFacing.Back)
                    {
                        index = i;
                        break;
                    }
                }
            }

            var camInfo = new Camera.CameraInfo();
            Camera.GetCameraInfo(index, camInfo);
            var isFront = camInfo.Facing == CameraFacing.Front;
            var jpegRotation = ComputeBufferRotationDegrees(camInfo);

            _camera = Camera.Open(index);
            try { _camera.SetDisplayOrientation(ComputeDisplayOrientation(camInfo)); }
            catch { /* 无 Surface 时部分机型忽略 */ }

            var p = _camera.GetParameters()!;
            var sizes = p.SupportedPreviewSizes;
            Camera.Size? best = null;
            if (sizes != null)
            {
                foreach (var s in sizes)
                {
                    if (s.Width * s.Height > 1280 * 720) continue;
                    if (best == null || s.Width * s.Height > best.Width * best.Height)
                        best = s;
                }
            }

            if (best != null)
                p.SetPreviewSize(best.Width, best.Height);
            p.PreviewFormat = ImageFormatType.Nv21;
            _camera.SetParameters(p);
            _previewTex = new SurfaceTexture(0);
            _camera.SetPreviewTexture(_previewTex);
            var w = best?.Width ?? 640;
            var h = best?.Height ?? 480;
            _lastJpegMs = 0;
            _camera.SetPreviewCallback(new PreviewCb((data, _) =>
            {
                if (!_camRun || data == null) return;
                var now = System.Environment.TickCount64;
                if (now - _lastJpegMs < 120) return;
                _lastJpegMs = now;
                try
                {
                    var yuv = new YuvImage(data, ImageFormatType.Nv21, w, h, null);
                    using var ms = new MemoryStream();
                    yuv.CompressToJpeg(new ARect(0, 0, w, h), 50, ms);
                    var jpeg = OrientJpeg(ms.ToArray(), jpegRotation, mirrorFront: isFront);
                    _svc.PostCameraJpeg(jpeg);
                }
                catch { /* ignore */ }
            }));
            _camRun = true;
            _camera.StartPreview();
            _svc.PublishStatus("本机摄像头测试推流中");
        }
    }

    /// <summary>预览回调缓冲相对「竖持手机抬头朝上」需旋转的角度。</summary>
    private static int ComputeBufferRotationDegrees(Camera.CameraInfo info)
    {
        var display = GetDisplayRotationDegrees();
        if (info.Facing == CameraFacing.Front)
            return (info.Orientation + display) % 360;
        return (info.Orientation - display + 360) % 360;
    }

    /// <summary>Camera.SetDisplayOrientation 用（与系统文档一致）。</summary>
    private static int ComputeDisplayOrientation(Camera.CameraInfo info)
    {
        var degrees = GetDisplayRotationDegrees();
        if (info.Facing == CameraFacing.Front)
        {
            var result = (info.Orientation + degrees) % 360;
            return (360 - result) % 360;
        }

        return (info.Orientation - degrees + 360) % 360;
    }

    private static int GetDisplayRotationDegrees()
    {
        try
        {
            var wm = global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.WindowService) as global::Android.Views.IWindowManager;
            var rot = wm?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;
            return rot switch
            {
                SurfaceOrientation.Rotation90 => 90,
                SurfaceOrientation.Rotation180 => 180,
                SurfaceOrientation.Rotation270 => 270,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>将传感器朝向的 JPEG 转到竖持正立；前摄再水平镜像，与自拍预览一致。</summary>
    private static byte[] OrientJpeg(byte[] jpeg, int rotationDegrees, bool mirrorFront)
    {
        rotationDegrees = ((rotationDegrees % 360) + 360) % 360;
        if (rotationDegrees == 0 && !mirrorFront)
            return jpeg;

        Bitmap? src = null;
        Bitmap? rotated = null;
        Bitmap? mirrored = null;
        try
        {
            src = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length);
            if (src == null) return jpeg;

            Bitmap working = src;
            if (rotationDegrees != 0)
            {
                var m = new Matrix();
                m.PostRotate(rotationDegrees);
                rotated = Bitmap.CreateBitmap(src, 0, 0, src.Width, src.Height, m, true);
                if (rotated == null) return jpeg;
                working = rotated;
            }

            if (mirrorFront)
            {
                var m2 = new Matrix();
                m2.SetScale(-1f, 1f, working.Width / 2f, working.Height / 2f);
                mirrored = Bitmap.CreateBitmap(working, 0, 0, working.Width, working.Height, m2, true);
                if (mirrored == null) return jpeg;
                working = mirrored;
            }

            using var outMs = new MemoryStream();
            if (!working.Compress(Bitmap.CompressFormat.Jpeg!, 55, outMs))
                return jpeg;
            return outMs.ToArray();
        }
        catch
        {
            return jpeg;
        }
        finally
        {
            try { src?.Recycle(); } catch { /* ignore */ }
            try { rotated?.Recycle(); } catch { /* ignore */ }
            try { mirrored?.Recycle(); } catch { /* ignore */ }
        }
    }

    private void StopCamera()
    {
        lock (_gate) StopCameraUnlocked();
    }

    private void StopCameraUnlocked()
    {
        _camRun = false;
        try { _camera?.SetPreviewCallback(null); } catch { /* ignore */ }
        try { _camera?.StopPreview(); } catch { /* ignore */ }
        try { _camera?.Release(); } catch { /* ignore */ }
        _camera = null;
        try { _previewTex?.Release(); } catch { /* ignore */ }
        _previewTex = null;
    }

    private void StartMic()
    {
        StopMic();
        var min = AudioRecord.GetMinBufferSize(16000, ChannelIn.Mono, Encoding.Pcm16bit);
        _mic = new AudioRecord(AudioSource.VoiceCommunication, 16000, ChannelIn.Mono, Encoding.Pcm16bit,
            Math.Max(min, 3200));
        if (_mic.State != State.Initialized)
        {
            _svc.PublishStatus("无法打开本机麦克风");
            try { _mic.Release(); } catch { /* ignore */ }
            _mic = null;
            return;
        }

        TryPreferAudioDevice(_mic, SharedDeviceKinds.Microphone);
        _micRun = true;
        _mic.StartRecording();
        var rec = _mic;
        _micThread = new Thread(() =>
        {
            var buf = new short[1600];
            var bytes = new byte[buf.Length * 2];
            while (_micRun && rec != null)
            {
                try
                {
                    var n = rec.Read(buf, 0, buf.Length);
                    if (n <= 0) continue;
                    Buffer.BlockCopy(buf, 0, bytes, 0, n * 2);
                    var slice = new byte[n * 2];
                    Buffer.BlockCopy(bytes, 0, slice, 0, slice.Length);
                    _svc.PostMicAudio(slice, 16000, 1);
                }
                catch
                {
                    break;
                }
            }
        })
        { IsBackground = true, Name = "FlyScreen.SdMic" };
        _micThread.Start();
        _svc.PublishStatus("本机麦克风测试推流中");
    }

    private void StopMic()
    {
        _micRun = false;
        try { _micThread?.Join(500); } catch { /* ignore */ }
        _micThread = null;
        try { _mic?.Stop(); } catch { /* ignore */ }
        try { _mic?.Release(); } catch { /* ignore */ }
        _mic = null;
    }

    private async Task PlayToneAsync()
    {
        await Task.Run(() =>
        {
            const int rate = 44100;
            const double sec = 0.7;
            const double freq = 880;
            var samples = (int)(rate * sec);
            var pcm = new byte[samples * 2];
            for (var i = 0; i < samples; i++)
            {
                var t = i / (double)rate;
                var env = Math.Min(1.0, Math.Min(t / 0.02, (sec - t) / 0.05));
                var s = (short)(Math.Sin(2 * Math.PI * freq * t) * 0.4 * env * short.MaxValue);
                pcm[i * 2] = (byte)(s & 0xff);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
            }

            lock (_gate)
            {
                StopTone();
                var min = AudioTrack.GetMinBufferSize(rate, ChannelOut.Mono, Encoding.Pcm16bit);
                var attrs = new AudioAttributes.Builder()!
                    .SetUsage(AudioUsageKind.Media)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!;
                var fmt = new AudioFormat.Builder()!
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetSampleRate(rate)!
                    .SetChannelMask(ChannelOut.Mono)!
                    .Build()!;
                _toneTrack = new AudioTrack.Builder()!
                    .SetAudioAttributes(attrs)!
                    .SetAudioFormat(fmt)!
                    .SetBufferSizeInBytes(Math.Max(min, pcm.Length))!
                    .SetTransferMode(AudioTrackMode.Static)!
                    .Build()!;
                TryPreferAudioDevice(_toneTrack, SharedDeviceKinds.Speaker);
                _toneTrack.Write(pcm, 0, pcm.Length);
                _toneTrack.Play();
            }

            Thread.Sleep((int)(sec * 1000) + 50);
            StopTone();
        });
        _svc.PublishStatus("本机扬声器测试音已播放");
    }

    private void StopTone()
    {
        try { _toneTrack?.Stop(); } catch { /* ignore */ }
        try { _toneTrack?.Release(); } catch { /* ignore */ }
        _toneTrack = null;
    }

    private void TryPreferAudioDevice(object trackOrRecord, string kind)
    {
        try
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.M) return;
            var idStr = _svc.GetSelectedDeviceId(kind);
            if (string.IsNullOrEmpty(idStr) || idStr == "default" || !int.TryParse(idStr, out var id))
                return;
            var am = global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.AudioService) as AudioManager;
            if (am == null) return;
            var isMic = kind.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase);
            var flags = isMic ? GetDevicesTargets.Inputs : GetDevicesTargets.Outputs;
            AudioDeviceInfo? match = null;
            var devices = am.GetDevices(flags);
            if (devices != null)
            {
                foreach (var d in devices)
                {
                    if (d != null && d.Id == id)
                    {
                        match = d;
                        break;
                    }
                }
            }

            if (match == null) return;
            if (trackOrRecord is AudioRecord rec)
                rec.SetPreferredDevice(match);
            else if (trackOrRecord is AudioTrack track)
                track.SetPreferredDevice(match);
        }
        catch { /* ignore */ }
    }

    private sealed class PreviewCb(Action<byte[]?, Camera?> onFrame) : Java.Lang.Object, Camera.IPreviewCallback
    {
        public void OnPreviewFrame(byte[]? data, Camera? camera) => onFrame(data, camera);
    }
}
#endif
