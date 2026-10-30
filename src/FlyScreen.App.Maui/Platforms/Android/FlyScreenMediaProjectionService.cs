#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using FlyScreen.Services;
using FlyScreen.Services.ScreenExtend;
using Image = Android.Media.Image;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 手机投影主机：持有 MediaProjection + VirtualDisplay，把屏幕帧 JPEG 推给 ScreenExtend。
/// Android 10+ 要求 MediaProjection 绑定前台服务（mediaProjection 类型）。
/// </summary>
[Service(
    Exported = false,
    Name = "flyscreen.media.FlyScreenMediaProjectionService",
    ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public sealed class FlyScreenMediaProjectionService : Service
{
    public const string ActionStart = "flyscreen.media.START";
    public const string ActionStop = "flyscreen.media.STOP";
    private const int NotifyId = 47;
    private const string ChannelId = "flyscreen.projection";
    private const int JpegQuality = 75;
    private const long MinFrameIntervalMs = 80;

    public static FlyScreenMediaProjectionService? Instance { get; private set; }

    private static readonly object Gate = new();
    private static int s_resultCode;
    private static Intent? s_resultData;
    private static int s_width = 1080;
    private static int s_height = 1920;
    private static int s_dpi = 320;
    private static ScreenExtendService? s_screenExtend;
    private static TaskCompletionSource<bool>? s_started;

    private MediaProjection? _projection;
    private VirtualDisplay? _virtualDisplay;
    private ImageReader? _reader;
    private HandlerThread? _readerThread;
    private Handler? _readerHandler;
    private MediaProjection.Callback? _projectionCb;
    private ImageReader.IOnImageAvailableListener? _imageListener;
    private long _lastFrameMs;
    private bool _foregrounded;
    private Bitmap? _scratchBitmap;
    private Bitmap? _frameBitmap;

    /// <summary>启动投屏前台服务；须先经系统授权拿到 resultCode/data。</summary>
    public static Task<bool> StartAsync(
        ScreenExtendService screenExtend,
        int resultCode,
        Intent data,
        int width,
        int height,
        int dpi)
    {
        var ctx = global::Android.App.Application.Context
                  ?? throw new InvalidOperationException("无 Application Context");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            s_screenExtend = screenExtend;
            s_resultCode = resultCode;
            s_resultData = data;
            s_width = Math.Max(160, width);
            s_height = Math.Max(160, height);
            s_dpi = Math.Max(120, dpi);
            s_started = tcs;
        }

        var intent = new Intent(ctx, typeof(FlyScreenMediaProjectionService));
        intent.SetAction(ActionStart);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                ctx.StartForegroundService(intent);
            else
                ctx.StartService(intent);
        }
        catch (Exception ex)
        {
            AppLog.Warn("投影前台服务启动失败: " + ex.Message);
            tcs.TrySetResult(false);
        }

        return tcs.Task;
    }

    public static void Stop()
    {
        var ctx = global::Android.App.Application.Context;
        if (ctx == null) return;
        try
        {
            var intent = new Intent(ctx, typeof(FlyScreenMediaProjectionService));
            intent.SetAction(ActionStop);
            ctx.StartService(intent);
        }
        catch
        {
            try { Instance?.StopSelf(); } catch { /* ignore */ }
        }
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action;
        if (string.Equals(action, ActionStop, StringComparison.Ordinal))
        {
            TearDown();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        EnterForeground();
        if (!TryBeginProjection())
        {
            s_started?.TrySetResult(false);
            StopSelf();
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        TearDown();
        if (ReferenceEquals(Instance, this))
            Instance = null;
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void EnterForeground()
    {
        if (_foregrounded) return;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var manager = GetSystemService(NotificationService) as NotificationManager;
                manager?.CreateNotificationChannel(new NotificationChannel(
                    ChannelId, "屏幕投影", NotificationImportance.Low));
            }

            var notification = new Notification.Builder(this, ChannelId)
                .SetContentTitle("玄鸟")
                .SetContentText("正在投影到电脑")
                .SetSmallIcon(global::Android.Resource.Drawable.IcMenuShare)
                .SetOngoing(true)
                .Build();

            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotifyId, notification, ForegroundService.TypeMediaProjection);
            else
                StartForeground(NotifyId, notification);

            _foregrounded = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("投影前台通知失败: " + ex.Message);
        }
    }

    private bool TryBeginProjection()
    {
        int resultCode;
        Intent? data;
        int w, h, dpi;
        ScreenExtendService? extend;
        lock (Gate)
        {
            resultCode = s_resultCode;
            data = s_resultData;
            w = s_width;
            h = s_height;
            dpi = s_dpi;
            extend = s_screenExtend;
        }

        if (data == null || extend == null)
        {
            AppLog.Warn("投影：缺少 MediaProjection 授权数据");
            return false;
        }

        try
        {
            var mpm = GetSystemService(MediaProjectionService) as MediaProjectionManager;
            if (mpm == null)
            {
                AppLog.Warn("投影：MediaProjectionManager 不可用");
                return false;
            }

            _projection = mpm.GetMediaProjection(resultCode, data);
            if (_projection == null)
            {
                AppLog.Warn("投影：GetMediaProjection 返回 null");
                return false;
            }

            _projectionCb = new ProjectionStopCallback(this);
            _projection.RegisterCallback(_projectionCb, new Handler(Looper.MainLooper!));

            _readerThread = new HandlerThread("FlyScreenProjectionReader");
            _readerThread.Start();
            _readerHandler = new Handler(_readerThread.Looper!);

            // PixelFormat.RGBA_8888 = 1
            _reader = ImageReader.NewInstance(w, h, (ImageFormatType)Format.Rgba8888, 2);
            _imageListener = new ImageAvailableListener(this, extend, w, h);
            _reader.SetOnImageAvailableListener(_imageListener, _readerHandler);

            // flags=0：MediaProjection 镜像；勿用 Display.Flag*
            _virtualDisplay = _projection.CreateVirtualDisplay(
                "FlyScreenCast",
                w, h, dpi,
                0,
                _reader.Surface,
                null,
                _readerHandler);

            s_started?.TrySetResult(true);
            AppLog.Info($"投影采集已启动 {w}x{h}@{dpi}dpi");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("投影采集启动失败: " + ex.Message);
            TearDown();
            return false;
        }
    }

    private void OnImage(ImageReader reader, ScreenExtendService extend, int width, int height)
    {
        Image? image = null;
        try
        {
            image = reader.AcquireLatestImage();
            if (image == null) return;

            var now = System.Environment.TickCount64;
            if (now - _lastFrameMs < MinFrameIntervalMs)
                return;
            _lastFrameMs = now;

            if (extend.Role != ScreenExtendRole.Host || !extend.IsActive)
                return;

            var jpeg = EncodeJpeg(image, width, height);
            if (jpeg is not { Length: > 0 }) return;

            _ = extend.SendFrameAsync(jpeg, width, height);
        }
        catch (Exception ex)
        {
            AppLog.Warn("投影帧编码失败: " + ex.Message);
        }
        finally
        {
            try { image?.Close(); } catch { /* ignore */ }
        }
    }

    private byte[]? EncodeJpeg(Image image, int width, int height)
    {
        var planes = image.GetPlanes();
        if (planes is not { Length: > 0 }) return null;
        var plane = planes[0];
        var buffer = plane.Buffer;
        if (buffer == null) return null;

        var pixelStride = plane.PixelStride;
        var rowStride = plane.RowStride;
        if (pixelStride <= 0) return null;

        var rowPadding = rowStride - pixelStride * width;
        var bmpW = width + (pixelStride > 0 ? rowPadding / pixelStride : 0);

        if (_scratchBitmap == null
            || _scratchBitmap.Width != bmpW
            || _scratchBitmap.Height != height
            || _scratchBitmap.IsRecycled)
        {
            _scratchBitmap?.Recycle();
            _scratchBitmap = Bitmap.CreateBitmap(bmpW, height, Bitmap.Config.Argb8888!);
        }

        buffer.Rewind();
        _scratchBitmap.CopyPixelsFromBuffer(buffer);

        Bitmap frame;
        if (bmpW != width)
        {
            if (_frameBitmap == null
                || _frameBitmap.Width != width
                || _frameBitmap.Height != height
                || _frameBitmap.IsRecycled)
            {
                _frameBitmap?.Recycle();
                _frameBitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
            }

            using var canvas = new Canvas(_frameBitmap);
            canvas.DrawBitmap(_scratchBitmap, 0, 0, null);
            frame = _frameBitmap;
        }
        else
        {
            frame = _scratchBitmap;
        }

        using var ms = new MemoryStream(Math.Max(16 * 1024, width * height / 8));
        if (!frame.Compress(Bitmap.CompressFormat.Jpeg!, JpegQuality, ms))
            return null;
        return ms.ToArray();
    }

    private void TearDown()
    {
        try { _reader?.SetOnImageAvailableListener(null, null); } catch { /* ignore */ }
        try { _virtualDisplay?.Release(); } catch { /* ignore */ }
        _virtualDisplay = null;
        try { _reader?.Close(); } catch { /* ignore */ }
        _reader = null;
        try
        {
            if (_projection != null && _projectionCb != null)
                _projection.UnregisterCallback(_projectionCb);
        }
        catch { /* ignore */ }
        try { _projection?.Stop(); } catch { /* ignore */ }
        _projection = null;
        _projectionCb = null;
        _imageListener = null;

        try
        {
            _readerThread?.QuitSafely();
            _readerThread?.Join(500);
        }
        catch { /* ignore */ }
        _readerThread = null;
        _readerHandler = null;

        try { _scratchBitmap?.Recycle(); } catch { /* ignore */ }
        try { _frameBitmap?.Recycle(); } catch { /* ignore */ }
        _scratchBitmap = null;
        _frameBitmap = null;

        lock (Gate)
        {
            s_resultData = null;
            s_started?.TrySetResult(false);
            s_started = null;
        }

        try
        {
            if (_foregrounded && OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
        }
        catch { /* ignore */ }
        _foregrounded = false;
    }

    private sealed class ProjectionStopCallback(FlyScreenMediaProjectionService owner)
        : MediaProjection.Callback
    {
        public override void OnStop()
        {
            AppLog.Info("系统已撤销屏幕录制授权");
            owner.TearDown();
            owner.StopSelf();
            try
            {
                var host = Services.MauiAppHost.EnsureCreated();
                if (host.ScreenExtend.IsActive)
                    _ = host.ScreenExtend.StopAsync("projection-revoked");
            }
            catch { /* ignore */ }
        }
    }

    private sealed class ImageAvailableListener(
        FlyScreenMediaProjectionService owner,
        ScreenExtendService extend,
        int width,
        int height) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader == null) return;
            owner.OnImage(reader, extend, width, height);
        }
    }
}
#endif
