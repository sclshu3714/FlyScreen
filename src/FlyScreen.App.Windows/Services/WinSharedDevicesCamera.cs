using System.Windows;
using System.Windows.Threading;
using FlyScreen.Services;
using FlyScreen.SharedDevices;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace FlyScreen.Services;

/// <summary>
/// Win 桌面提供端摄像头：与 Maui-Win 相同，走 MediaCapture（MF 枚举在本机常失败）。
/// </summary>
public static class WinSharedDevicesCamera
{
    public static void Register()
        => WindowsSharedDevicesProviderMedia.CameraCaptureOverride = Run;

    public static void Unregister()
    {
        if (WindowsSharedDevicesProviderMedia.CameraCaptureOverride == Run)
            WindowsSharedDevicesProviderMedia.CameraCaptureOverride = null;
    }

    private static bool Run(
        string? preferId,
        Func<bool> shouldContinue,
        Action<byte[]> postJpeg,
        Action<string> status)
    {
        var ready = new TaskCompletionSource<MediaPipe?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            AppLog.Warn("Win MediaCapture：无 UI Dispatcher");
            return false;
        }

        _ = dispatcher.BeginInvoke(async () =>
        {
            try { ready.TrySetResult(await OpenAsync(preferId).ConfigureAwait(true)); }
            catch (Exception ex)
            {
                AppLog.Warn("Win MediaCapture 打开失败: " + ex.Message);
                ready.TrySetResult(null);
            }
        }, DispatcherPriority.Normal);

        var pipe = ready.Task.GetAwaiter().GetResult();
        if (pipe == null)
            return false;

        try
        {
            status("本机摄像头测试推流中");
            var lastMs = 0L;
            while (shouldContinue())
            {
                if (!pipe.TryDequeue(out var jpeg) || jpeg.Length == 0)
                {
                    Thread.Sleep(20);
                    continue;
                }

                var now = Environment.TickCount64;
                if (now - lastMs < 120)
                    continue;
                lastMs = now;
                postJpeg(jpeg);
            }
        }
        finally
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dispatcher.BeginInvoke(async () =>
            {
                try { await pipe.DisposeAsync().ConfigureAwait(true); }
                catch { /* ignore */ }
                done.TrySetResult();
            }, DispatcherPriority.Normal);
            try { done.Task.Wait(3000); } catch { /* ignore */ }
        }

        return true;
    }

    private static async Task<MediaPipe?> OpenAsync(string? preferId)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        if (devices.Count == 0)
        {
            AppLog.Warn("Win MediaCapture：DeviceInformation 未找到摄像头");
            return null;
        }

        DeviceInformation? pick = null;
        if (!string.IsNullOrEmpty(preferId))
        {
            pick = devices.FirstOrDefault(d =>
                d.Id.Equals(preferId, StringComparison.OrdinalIgnoreCase)
                || IdsLikelyMatch(d.Id, preferId)
                || (!string.IsNullOrEmpty(d.Name)
                    && (preferId.Contains(d.Name, StringComparison.OrdinalIgnoreCase)
                        || d.Name.Equals(preferId, StringComparison.OrdinalIgnoreCase))));
        }

        pick ??= devices[0];
        AppLog.Info($"Win MediaCapture：使用 {pick.Name}");

        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = pick.Id,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        });

        var source = capture.FrameSources.Values.FirstOrDefault(s =>
                         s.Info.SourceKind == MediaFrameSourceKind.Color)
                     ?? capture.FrameSources.Values.FirstOrDefault();
        if (source == null)
        {
            capture.Dispose();
            AppLog.Warn("Win MediaCapture：无可用视频帧源");
            return null;
        }

        MediaFrameReader reader;
        try { reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8); }
        catch { reader = await capture.CreateFrameReaderAsync(source); }

        var pipe = new MediaPipe(capture, reader);
        reader.FrameArrived += pipe.OnFrameArrived;
        var start = await reader.StartAsync();
        if (start != MediaFrameReaderStartStatus.Success)
        {
            await pipe.DisposeAsync();
            AppLog.Warn("Win MediaCapture：FrameReader 启动失败 " + start);
            return null;
        }

        return pipe;
    }

    private static bool IdsLikelyMatch(string a, string b)
    {
        static string Norm(string s)
            => s.Replace('\\', '#').Replace('/', '#').ToUpperInvariant()
                .Replace(@"#?#USB#", "#USB#", StringComparison.Ordinal)
                .Replace(@"#?#", "#", StringComparison.Ordinal);

        var na = Norm(a);
        var nb = Norm(b);
        return na.Length >= 12 && nb.Length >= 12
               && (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal));
    }

    private sealed class MediaPipe : IAsyncDisposable
    {
        private readonly MediaCapture _capture;
        private readonly MediaFrameReader _reader;
        private readonly object _gate = new();
        private byte[]? _latest;
        private int _disposed;
        private int _encoding;

        public MediaPipe(MediaCapture capture, MediaFrameReader reader)
        {
            _capture = capture;
            _reader = reader;
        }

        public async void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            if (_disposed != 0 || Interlocked.CompareExchange(ref _encoding, 1, 0) != 0)
                return;

            try
            {
                using var frame = sender.TryAcquireLatestFrame();
                var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap == null) return;

                try
                {
                    var jpeg = await EncodeJpegAsync(bitmap).ConfigureAwait(false);
                    if (jpeg.Length == 0) return;
                    lock (_gate) _latest = jpeg;
                }
                finally
                {
                    bitmap.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Win MediaCapture 编码失败: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _encoding, 0);
            }
        }

        public bool TryDequeue(out byte[] jpeg)
        {
            lock (_gate)
            {
                jpeg = _latest ?? [];
                _latest = null;
                return jpeg.Length > 0;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _reader.FrameArrived -= OnFrameArrived; } catch { /* ignore */ }
            try { await _reader.StopAsync(); } catch { /* ignore */ }
            try { _reader.Dispose(); } catch { /* ignore */ }
            try { _capture.Dispose(); } catch { /* ignore */ }
        }

        private static async Task<byte[]> EncodeJpegAsync(SoftwareBitmap bitmap)
        {
            using var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                             && bitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? SoftwareBitmap.Copy(bitmap)
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            encoder.SetSoftwareBitmap(bgra);
            if (bgra.PixelWidth > 1280 || bgra.PixelHeight > 720)
            {
                encoder.BitmapTransform.ScaledWidth = 1280;
                encoder.BitmapTransform.ScaledHeight = 720;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            }

            await encoder.FlushAsync();
            var size = (uint)stream.Size;
            if (size == 0) return [];
            var buf = new byte[size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            reader.ReadBytes(buf);
            return buf;
        }
    }
}
