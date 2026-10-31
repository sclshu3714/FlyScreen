#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Views;
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
    private static int s_capLong = 1920;
    private static ScreenExtendService? s_screenExtend;
    private static TaskCompletionSource<bool>? s_started;

    private MediaProjection? _projection;
    internal MediaProjection? Projection => _projection;
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
    private int _width;
    private int _height;
    private int _dpi;
    private int _longEdge;
    private int _shortEdge;
    /// <summary>编码长边上限。虚拟屏本身保持手机真实像素。</summary>
    private int _capLong;
    private bool _loggedCrop;
    private int _closing;
    private ScreenExtendService? _extend;
    private DisplayManager? _displayManager;
    private DisplayManager.IDisplayListener? _displayListener;

    /// <summary>启动投屏前台服务；须先经系统授权拿到 resultCode/data。</summary>
    public static Task<bool> StartAsync(
        ScreenExtendService screenExtend,
        int resultCode,
        Intent data,
        int width,
        int height,
        int dpi,
        int capLong)
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
            s_dpi = Math.Max(1, dpi);
            s_capLong = Math.Max(160, capLong);
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

    public override void OnConfigurationChanged(Configuration? newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ScheduleOrientationSync();
    }

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
            _extend = extend;
            Interlocked.Exchange(ref _closing, 0);
            var fitted = ResolveCaptureSize(w, h, dpi);
            w = fitted.W;
            h = fitted.H;
            dpi = fitted.Dpi;
            _dpi = dpi;
            _longEdge = Math.Max(w, h);
            _shortEdge = Math.Min(w, h);

            if (!AttachCapture(w, h, dpi, extend))
            {
                TearDown();
                return false;
            }

            WatchDisplayRotation();
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

    /// <summary>
    /// 竖屏创建的虚拟屏在转到横屏后，小米等机会把横屏内容旋进原来的竖屏缓冲。
    /// 横屏创建则宽边足够，后续横竖切换都正常。朝向变化时按手机屏幕比例重建采集表面。
    /// </summary>
    private void ScheduleOrientationSync()
    {
        var handler = _readerHandler;
        if (handler == null || _projection == null) return;
        handler.PostDelayed(ApplyOrientation, 120);
    }

    private void ApplyOrientation()
    {
        if (Volatile.Read(ref _closing) != 0) return;
        if (_projection == null || _extend == null) return;
        if (_longEdge <= 0 || _shortEdge <= 0 || _longEdge == _shortEdge) return;

        var fitted = ResolveCaptureSize(
            landFallbackW(),
            landFallbackH(),
            _dpi);
        if (fitted.W == _width && fitted.H == _height && _virtualDisplay != null)
            return;

        AppLog.Info($"投影采集跟随朝向 {_width}x{_height} → {fitted.W}x{fitted.H}");
        if (!AttachCapture(fitted.W, fitted.H, fitted.Dpi, _extend))
            AppLog.Warn("投影采集朝向调整失败");

        int landFallbackW()
        {
            var land = CurrentIsLandscape();
            if (land == true) return _longEdge;
            if (land == false) return _shortEdge;
            return _width;
        }

        int landFallbackH()
        {
            var land = CurrentIsLandscape();
            if (land == true) return _shortEdge;
            if (land == false) return _longEdge;
            return _height;
        }
    }

    /// <summary>虚拟屏用手机真实像素和真实 dpi。缩小或改 dpi 会让小米把画面居中缩小，四边同时留黑。</summary>
    private (int W, int H, int Dpi) ResolveCaptureSize(int fallbackW, int fallbackH, int fallbackDpi)
    {
        _capLong = s_capLong > 0 ? s_capLong : 1920;
        if (!TryReadScreen(out var sw, out var sh, out var deviceDpi))
            return (Math.Max(160, fallbackW), Math.Max(160, fallbackH), Math.Max(1, fallbackDpi));

        _longEdge = Math.Max(sw, sh);
        _shortEdge = Math.Min(sw, sh);
        return (sw, sh, deviceDpi);
    }

    private bool TryReadScreen(out int w, out int h, out int dpi)
    {
        w = 0;
        h = 0;
        dpi = 320;
        try
        {
            var dm = Resources?.DisplayMetrics;
            if (dm != null)
                dpi = (int)dm.DensityDpi;

            var size = new global::Android.Graphics.Point();
            global::Android.Views.Display? display = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                display = Display;
            display ??= (GetSystemService(WindowService) as IWindowManager)?.DefaultDisplay;
            display?.GetRealSize(size);
            if (size.X > 0 && size.Y > 0)
            {
                w = size.X;
                h = size.Y;
            }
            else if (dm != null)
            {
                w = dm.WidthPixels;
                h = dm.HeightPixels;
            }
        }
        catch
        {
            return false;
        }

        if (w <= 0 || h <= 0)
            return false;

        var orient = Resources?.Configuration?.Orientation;
        if (orient == global::Android.Content.Res.Orientation.Portrait && w > h)
            (w, h) = (h, w);
        else if (orient == global::Android.Content.Res.Orientation.Landscape && w < h)
            (w, h) = (h, w);

        dpi = Math.Max(1, dpi);
        return true;
    }

    private bool? CurrentIsLandscape()
    {
        try
        {
            var orient = Resources?.Configuration?.Orientation;
            if (orient == global::Android.Content.Res.Orientation.Landscape) return true;
            if (orient == global::Android.Content.Res.Orientation.Portrait) return false;
        }
        catch
        {
            // ignore
        }

        try
        {
            var wm = GetSystemService(WindowService) as IWindowManager;
            var rot = wm?.DefaultDisplay?.Rotation;
            if (rot is SurfaceOrientation.Rotation90 or SurfaceOrientation.Rotation270)
                return true;
            if (rot is SurfaceOrientation.Rotation0 or SurfaceOrientation.Rotation180)
                return false;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private void WatchDisplayRotation()
    {
        try
        {
            _displayManager = GetSystemService(DisplayService) as DisplayManager;
            if (_displayManager == null) return;
            _displayListener ??= new ProjectionDisplayListener(this);
            _displayManager.RegisterDisplayListener(_displayListener, new Handler(Looper.MainLooper!));
        }
        catch (Exception ex)
        {
            AppLog.Warn("投影朝向监听失败: " + ex.Message);
        }
    }

    /// <summary>释放旧表面并用新尺寸重建。须在采集线程上调用，避免与取帧重入。</summary>
    private bool AttachCapture(int w, int h, int dpi, ScreenExtendService extend)
    {
        if (Volatile.Read(ref _closing) != 0 || _projection == null)
            return false;
        ReleaseCaptureSurface();
        try
        {
            // PixelFormat.RGBA_8888 = 1；flags=0：MediaProjection 镜像，勿用 Display.Flag*
            _reader = ImageReader.NewInstance(w, h, (ImageFormatType)Format.Rgba8888, 2);
            _imageListener = new ImageAvailableListener(this, extend, w, h);
            _reader.SetOnImageAvailableListener(_imageListener, _readerHandler);
            _virtualDisplay = _projection!.CreateVirtualDisplay(
                "FlyScreenCast",
                w, h, dpi,
                0,
                _reader.Surface,
                null,
                _readerHandler);
            if (Volatile.Read(ref _closing) != 0)
            {
                ReleaseCaptureSurface();
                return false;
            }

            _width = w;
            _height = h;
            _dpi = dpi;
            _loggedCrop = false;
            RecycleBitmaps();
            return _virtualDisplay != null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("投影采集表面重建失败: " + ex.Message);
            ReleaseCaptureSurface();
            return false;
        }
    }

    private void ReleaseCaptureSurface()
    {
        try { _reader?.SetOnImageAvailableListener(null, null); } catch { /* ignore */ }
        try { _virtualDisplay?.Release(); } catch { /* ignore */ }
        _virtualDisplay = null;
        try { _reader?.Close(); } catch { /* ignore */ }
        _reader = null;
        _imageListener = null;
    }

    private void RecycleBitmaps()
    {
        try { _scratchBitmap?.Recycle(); } catch { /* ignore */ }
        try { _frameBitmap?.Recycle(); } catch { /* ignore */ }
        _scratchBitmap = null;
        _frameBitmap = null;
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

            var jpeg = EncodeJpeg(image, width, height, out var outW, out var outH);
            if (jpeg is not { Length: > 0 }) return;

            _ = extend.SendFrameAsync(jpeg, outW, outH);
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

    private byte[]? EncodeJpeg(Image image, int width, int height, out int outW, out int outH)
    {
        outW = width;
        outH = height;
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

        var content = CropUniformBlackBorder(frame);
        var output = ScaleToLongEdge(content, _capLong > 0 ? _capLong : s_capLong);
        outW = output.Width;
        outH = output.Height;
        try
        {
            using var ms = new MemoryStream(Math.Max(16 * 1024, outW * outH / 8));
            if (!output.Compress(Bitmap.CompressFormat.Jpeg!, JpegQuality, ms))
                return null;
            return ms.ToArray();
        }
        finally
        {
            if (!ReferenceEquals(output, content) && !ReferenceEquals(output, frame) && !ReferenceEquals(output, _scratchBitmap))
                output.Recycle();
            if (!ReferenceEquals(content, frame) && !ReferenceEquals(content, _scratchBitmap))
                content.Recycle();
        }
    }

    /// <summary>
    /// 四边都是黑边时裁掉。这是虚拟屏把画面缩小后居中留下的，不是影片自己的上下黑边。
    /// </summary>
    private Bitmap CropUniformBlackBorder(Bitmap source)
    {
        const int lumaMax = 10;
        var w = source.Width;
        var h = source.Height;
        if (w < 32 || h < 32) return source;

        var maxInsetX = w / 4;
        var maxInsetY = h / 4;
        var top = MeasureBorder(source, horizontal: true, fromStart: true, lumaMax, maxInsetY);
        var bottom = MeasureBorder(source, horizontal: true, fromStart: false, lumaMax, maxInsetY);
        var left = MeasureBorder(source, horizontal: false, fromStart: true, lumaMax, maxInsetX);
        var right = MeasureBorder(source, horizontal: false, fromStart: false, lumaMax, maxInsetX);
        if (top <= 2 || bottom <= 2 || left <= 2 || right <= 2)
            return source;

        var cw = w - left - right;
        var ch = h - top - bottom;
        if (cw < 160 || ch < 160)
            return source;

        if (!_loggedCrop)
        {
            _loggedCrop = true;
            AppLog.Info($"投影裁掉四边黑边 {w}x{h} → {cw}x{ch}");
        }

        var cropped = Bitmap.CreateBitmap(cw, ch, Bitmap.Config.Argb8888!);
        if (cropped == null) return source;
        using (var canvas = new Canvas(cropped))
            canvas.DrawBitmap(source, -left, -top, null);
        return cropped;
    }

    private static int MeasureBorder(Bitmap bmp, bool horizontal, bool fromStart, int lumaMax, int maxInset)
    {
        var limit = horizontal ? bmp.Height : bmp.Width;
        var inset = 0;
        while (inset < maxInset && inset < limit)
        {
            var pos = fromStart ? inset : limit - 1 - inset;
            if (!LineIsBlack(bmp, horizontal, pos, lumaMax))
                break;
            inset++;
        }

        return inset;
    }

    private static bool LineIsBlack(Bitmap bmp, bool horizontal, int pos, int lumaMax)
    {
        var length = horizontal ? bmp.Width : bmp.Height;
        var step = Math.Max(1, length / 48);
        for (var i = 0; i < length; i += step)
        {
            var color = horizontal ? bmp.GetPixel(i, pos) : bmp.GetPixel(pos, i);
            var r = (color >> 16) & 0xFF;
            var g = (color >> 8) & 0xFF;
            var b = color & 0xFF;
            var luma = (r * 54 + g * 183 + b * 19) >> 8;
            if (luma >= lumaMax)
                return false;
        }

        return true;
    }

    private static Bitmap ScaleToLongEdge(Bitmap source, int capLong)
    {
        if (capLong < 160) capLong = 1920;
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= capLong)
            return source;

        var scale = capLong / (double)longEdge;
        var dw = Math.Max(2, (int)Math.Round(source.Width * scale));
        var dh = Math.Max(2, (int)Math.Round(source.Height * scale));
        if ((dw & 1) == 1) dw--;
        if ((dh & 1) == 1) dh--;
        return Bitmap.CreateScaledBitmap(source, Math.Max(2, dw), Math.Max(2, dh), true) ?? source;
    }

    private void TearDown()
    {
        AndroidProjectionAudio.StopAny();
        Interlocked.Exchange(ref _closing, 1);
        _longEdge = 0;
        _shortEdge = 0;
        _capLong = 0;
        try
        {
            if (_displayManager != null && _displayListener != null)
                _displayManager.UnregisterDisplayListener(_displayListener);
        }
        catch { /* ignore */ }
        _displayManager = null;

        ReleaseCaptureSurface();
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

        RecycleBitmaps();
        _extend = null;

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

    private sealed class ProjectionDisplayListener(FlyScreenMediaProjectionService owner)
        : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        public void OnDisplayAdded(int displayId) { }

        public void OnDisplayChanged(int displayId)
        {
            if (displayId != (int)Display.DefaultDisplay) return;
            owner.ScheduleOrientationSync();
        }

        public void OnDisplayRemoved(int displayId) { }
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
