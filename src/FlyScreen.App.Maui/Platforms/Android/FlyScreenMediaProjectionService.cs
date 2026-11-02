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
    /// <summary>未指定分辨率时的编码长边。用户所选分辨率更高时按所选值编码。</summary>
    private const int EncodeLongEdge = 1920;
    /// <summary>去黑边已稳住后，不必每帧重测边缘。</summary>
    private const long CropMeasureIntervalMs = 500;

    public static FlyScreenMediaProjectionService? Instance { get; private set; }

    private static readonly object Gate = new();
    private static int s_resultCode;
    private static Intent? s_resultData;
    private static int s_width = 1080;
    private static int s_height = 1920;
    private static int s_dpi = 320;
    private static int s_capLong = 1920;
    private static bool s_cropBlackBars;
    private static int s_jpegQuality = ProjectionDisplayPresets.DefaultJpegQuality;
    private static int s_frameIntervalMs = ProjectionDisplayPresets.FpsToIntervalMs(ProjectionDisplayPresets.DefaultFps);
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
    private int _encodeBusy;
    private int[]? _lineBuf;
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
    private bool _cropBlackBars;
    private int _jpegQuality = ProjectionDisplayPresets.DefaultJpegQuality;
    private int _frameIntervalMs = ProjectionDisplayPresets.FpsToIntervalMs(ProjectionDisplayPresets.DefaultFps);
    private readonly ProjectionCropHold _cropHold = new();
    private Bitmap? _scaledBitmap;
    private Bitmap? _croppedBitmap;
    private ProjectionBlackBorder _stableCrop;
    private bool _hasStableCrop;
    private int _cropFrameW;
    private int _cropFrameH;
    private long _lastMeasureMs;
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
        int capLong,
        bool cropBlackBars,
        int fps = ProjectionDisplayPresets.DefaultFps,
        int jpegQuality = ProjectionDisplayPresets.DefaultJpegQuality)
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
            s_cropBlackBars = cropBlackBars;
            s_jpegQuality = ProjectionDisplayPresets.ClampJpegQuality(jpegQuality);
            s_frameIntervalMs = ProjectionDisplayPresets.FpsToIntervalMs(fps);
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
        bool cropBlackBars;
        int jpegQuality;
        int frameIntervalMs;
        lock (Gate)
        {
            resultCode = s_resultCode;
            data = s_resultData;
            w = s_width;
            h = s_height;
            dpi = s_dpi;
            extend = s_screenExtend;
            cropBlackBars = s_cropBlackBars;
            jpegQuality = s_jpegQuality;
            frameIntervalMs = s_frameIntervalMs;
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
            _cropBlackBars = cropBlackBars;
            _jpegQuality = jpegQuality;
            _frameIntervalMs = frameIntervalMs;
            ResetCropState();
            _loggedCrop = false;
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
            AppLog.Info($"投影采集已启动 {w}x{h}@{dpi}dpi · {1000 / Math.Max(1, frameIntervalMs)}帧 · 画质 {jpegQuality}"
                        + (cropBlackBars ? " · 去黑边" : ""));
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
            ResetCropState();
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
        try { _scaledBitmap?.Recycle(); } catch { /* ignore */ }
        try { _croppedBitmap?.Recycle(); } catch { /* ignore */ }
        _scratchBitmap = null;
        _frameBitmap = null;
        _scaledBitmap = null;
        _croppedBitmap = null;
    }

    private void OnImage(ImageReader reader, ScreenExtendService extend, int width, int height)
    {
        Image? image = null;
        var handedOff = false;
        try
        {
            image = reader.AcquireLatestImage();
            if (image == null) return;
            if (Volatile.Read(ref _closing) != 0) return;

            var now = System.Environment.TickCount64;
            if (now - _lastFrameMs < _frameIntervalMs)
                return;
            // 上一帧还在缩放/编码时丢掉新帧。编码耗时不稳时硬排会让接收端一顿一顿。
            if (Interlocked.CompareExchange(ref _encodeBusy, 1, 0) != 0)
                return;
            if (extend.Role != ScreenExtendRole.Host || !extend.IsActive)
            {
                Interlocked.Exchange(ref _encodeBusy, 0);
                return;
            }

            if (!CopyFrame(image, width, height))
            {
                Interlocked.Exchange(ref _encodeBusy, 0);
                return;
            }

            _lastFrameMs = now;
            handedOff = true;
            var frameW = width;
            var frameH = height;
            _ = Task.Run(() => EncodeAndSend(extend, frameW, frameH));
        }
        catch (Exception ex)
        {
            if (!handedOff)
                Interlocked.Exchange(ref _encodeBusy, 0);
            AppLog.Warn("投影帧编码失败: " + ex.Message);
        }
        finally
        {
            try { image?.Close(); } catch { /* ignore */ }
        }
    }

    private void EncodeAndSend(ScreenExtendService extend, int width, int height)
    {
        try
        {
            var jpeg = FinishJpeg(width, height, out var outW, out var outH);
            if (jpeg is { Length: > 0 })
                extend.SendFrameAsync(jpeg, outW, outH).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLog.Warn("投影帧发送失败: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _encodeBusy, 0);
        }
    }

    /// <summary>把 Image 像素拷进可复用位图。必须在关闭 Image 之前完成。</summary>
    private bool CopyFrame(Image image, int width, int height)
    {
        var planes = image.GetPlanes();
        if (planes is not { Length: > 0 }) return false;
        var plane = planes[0];
        var buffer = plane.Buffer;
        if (buffer == null) return false;

        var pixelStride = plane.PixelStride;
        var rowStride = plane.RowStride;
        if (pixelStride <= 0) return false;

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

        if (_scratchBitmap == null) return false;
        buffer.Rewind();
        _scratchBitmap.CopyPixelsFromBuffer(buffer);
        return true;
    }

    private byte[]? FinishJpeg(int width, int height, out int outW, out int outH)
    {
        outW = width;
        outH = height;
        if (_scratchBitmap == null || _scratchBitmap.IsRecycled) return null;

        var bmpW = _scratchBitmap.Width;
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

            if (_frameBitmap == null) return null;
            using var canvas = new Canvas(_frameBitmap);
            canvas.DrawBitmap(_scratchBitmap, 0, 0, null);
            frame = _frameBitmap;
        }
        else
        {
            frame = _scratchBitmap;
        }

        var picture = ProjectionDisplayPresets.DescribeQuality(_jpegQuality);
        Bitmap scaled;
        if (picture.Passthrough)
            scaled = frame;
        else
        {
            var cap = _capLong > 0 ? _capLong : s_capLong;
            if (picture.ExtraLongEdgeCap > 0)
                cap = Math.Min(cap, picture.ExtraLongEdgeCap);
            scaled = ScaleToLongEdge(frame, cap);
        }

        var output = ApplyCrop(scaled);
        outW = output.Width;
        outH = output.Height;
        using var ms = new MemoryStream(Math.Max(16 * 1024, outW * outH / 6));
        if (!output.Compress(Bitmap.CompressFormat.Jpeg!, picture.EncodeQuality, ms))
            return null;
        return ms.ToArray();
    }

    private void ResetCropState()
    {
        _cropHold.Reset();
        _hasStableCrop = false;
        _cropFrameW = 0;
        _cropFrameH = 0;
        _lastMeasureMs = 0;
    }

    /// <summary>
    /// 把从机播放器内容区坐标映射到手机全屏 0..1。
    /// 去黑边开启时，播放器看到的是裁切后画面，需加回黑边偏移。
    /// </summary>
    public static void MapReverseInputToScreen(double contentNx, double contentNy, out double screenNx, out double screenNy)
    {
        screenNx = contentNx;
        screenNy = contentNy;
        var svc = Instance;
        if (svc == null || !svc._cropBlackBars || !svc._hasStableCrop)
            return;

        var fw = svc._cropFrameW;
        var fh = svc._cropFrameH;
        if (fw <= 1 || fh <= 1)
            return;

        svc._stableCrop.MapContentToFrame(contentNx, contentNy, fw, fh, out screenNx, out screenNy);
    }

    /// <summary>
    /// 裁掉虚拟屏四边缩进，以及播放器成对的宽黑边。状态栏这种单边细条留着。
    /// </summary>
    private Bitmap ApplyCrop(Bitmap source)
    {
        if (!_cropBlackBars)
            return source;

        var w = source.Width;
        var h = source.Height;
        if (w < 32 || h < 32) return source;

        var now = System.Environment.TickCount64;
        ProjectionBlackBorder crop;
        if (_hasStableCrop
            && _cropFrameW == w
            && _cropFrameH == h
            && now - _lastMeasureMs < CropMeasureIntervalMs)
        {
            crop = _stableCrop;
        }
        else
        {
            const int lumaMax = 10;
            var top = MeasureBorder(source, horizontal: true, fromStart: true, lumaMax);
            var bottom = MeasureBorder(source, horizontal: true, fromStart: false, lumaMax);
            var left = MeasureBorder(source, horizontal: false, fromStart: true, lumaMax);
            var right = MeasureBorder(source, horizontal: false, fromStart: false, lumaMax);
            var measured = ProjectionBlackBorder.FromInsets(w, h, left, top, right, bottom);
            crop = _cropHold.Stabilize(w, h, measured);
            _lastMeasureMs = now;
            if (!crop.IsFullFrame(w, h))
            {
                _stableCrop = crop;
                _cropFrameW = w;
                _cropFrameH = h;
                _hasStableCrop = true;
            }
            else
            {
                _hasStableCrop = false;
                _cropFrameW = 0;
                _cropFrameH = 0;
            }
        }

        if (crop.IsFullFrame(w, h) || crop.Width > w || crop.Height > h)
            return source;

        if (_croppedBitmap == null
            || _croppedBitmap.Width != crop.Width
            || _croppedBitmap.Height != crop.Height
            || _croppedBitmap.IsRecycled)
        {
            _croppedBitmap?.Recycle();
            _croppedBitmap = Bitmap.CreateBitmap(crop.Width, crop.Height, Bitmap.Config.Argb8888!);
        }

        if (_croppedBitmap == null) return source;
        if (!_loggedCrop)
        {
            _loggedCrop = true;
            AppLog.Info($"投影裁掉黑边 {w}x{h} → {crop.Width}x{crop.Height}");
        }

        using (var canvas = new Canvas(_croppedBitmap))
            canvas.DrawBitmap(source, -crop.Left, -crop.Top, null);
        return _croppedBitmap;
    }

    /// <summary>
    /// 从边缘向内找第一条不是黑的线。45% 以内碰不到画面就不动，避免把深色内容切掉。
    /// </summary>
    private int MeasureBorder(Bitmap bmp, bool horizontal, bool fromStart, int lumaMax)
    {
        var span = horizontal ? bmp.Height : bmp.Width;
        var maxInset = Math.Min(span * 45 / 100, span - 1);
        if (maxInset <= 0) return 0;

        var startPos = fromStart ? 0 : span - 1;
        if (!LineIsBlack(bmp, horizontal, startPos, lumaMax))
            return 0;

        var far = maxInset - 1;
        var farPos = fromStart ? far : span - 1 - far;
        if (LineIsBlack(bmp, horizontal, farPos, lumaMax))
            return 0;

        var lo = 0;
        var hi = far;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            var pos = fromStart ? mid : span - 1 - mid;
            if (LineIsBlack(bmp, horizontal, pos, lumaMax))
                lo = mid;
            else
                hi = mid;
        }

        return hi;
    }

    private bool LineIsBlack(Bitmap bmp, bool horizontal, int pos, int lumaMax)
    {
        var length = horizontal ? bmp.Width : bmp.Height;
        if (length <= 0) return true;
        if (_lineBuf == null || _lineBuf.Length < length)
            _lineBuf = new int[length];

        try
        {
            if (horizontal)
                bmp.GetPixels(_lineBuf, 0, length, 0, pos, length, 1);
            else
                bmp.GetPixels(_lineBuf, 0, 1, pos, 0, 1, length);
        }
        catch
        {
            return false;
        }

        var step = Math.Max(1, length / 48);
        var samples = 0;
        var bright = 0;
        for (var i = 0; i < length; i += step)
        {
            samples++;
            var color = _lineBuf[i];
            var r = (color >> 16) & 0xFF;
            var g = (color >> 8) & 0xFF;
            var b = color & 0xFF;
            var luma = (r * 54 + g * 183 + b * 19) >> 8;
            if (luma >= lumaMax)
                bright++;
        }

        // 弹幕扫过黑边时会有少量亮点，不能因此把整条边判成画面。
        return samples == 0 || bright * 5 <= samples;
    }

    private Bitmap ScaleToLongEdge(Bitmap source, int capLong)
    {
        if (capLong < 160) capLong = EncodeLongEdge;
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= capLong)
            return source;

        var scale = capLong / (double)longEdge;
        var dw = Math.Max(2, (int)Math.Round(source.Width * scale));
        var dh = Math.Max(2, (int)Math.Round(source.Height * scale));
        if ((dw & 1) == 1) dw--;
        if ((dh & 1) == 1) dh--;
        dw = Math.Max(2, dw);
        dh = Math.Max(2, dh);
        if (_scaledBitmap == null
            || _scaledBitmap.Width != dw
            || _scaledBitmap.Height != dh
            || _scaledBitmap.IsRecycled)
        {
            _scaledBitmap?.Recycle();
            _scaledBitmap = Bitmap.CreateBitmap(dw, dh, Bitmap.Config.Argb8888!);
        }

        if (_scaledBitmap == null) return source;
        using (var canvas = new Canvas(_scaledBitmap))
            canvas.DrawBitmap(source, null, new global::Android.Graphics.Rect(0, 0, dw, dh), null);
        return _scaledBitmap;
    }

    private void TearDown()
    {
        AndroidProjectionAudio.StopAny();
        Interlocked.Exchange(ref _closing, 1);
        _longEdge = 0;
        _shortEdge = 0;
        _capLong = 0;
        ResetCropState();
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
