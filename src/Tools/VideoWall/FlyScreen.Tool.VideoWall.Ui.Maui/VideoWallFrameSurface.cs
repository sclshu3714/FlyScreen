using System.Reflection;
using System.Runtime.InteropServices;
#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
#endif
using FlyScreen.Services;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>
/// Maui 软件帧播放面：LibVLC RV32 → 画面。
/// Windows：WriteableBitmap；Android：直接写 MAUI Image 的平台 ImageView（SetPixels），
/// 避免 Image.Source 闪黑，也避免往 ContentView 上 AddView（MAUI 不给额外子视图布局 → 0×0）。
/// </summary>
public sealed class VideoWallFrameSurface : ContentView, IVideoFrameSink
{
    private readonly Image _image;
    private readonly object _gate = new();
    private byte[] _pending = [];
    private int[] _argbScratch = [];
    private int _width;
    private int _height;
    private int _stride;
    private int _dirty;
    private int _scheduled;
    private readonly int _minFrameIntervalMs;
    private long _lastAcceptTick;
    private int _androidOkLogged;
    private int _androidFailLogged;

#if WINDOWS
    private Microsoft.UI.Xaml.Controls.Image? _nativeImage;
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _bitmap;
    private int _winHooked;
#endif

    private object? _androidBitmap;
    private object? _androidImageView;

    /// <param name="maxFps">出画上限；0 表示不限（单路）。宫格建议 12–20。</param>
    public VideoWallFrameSurface(int maxFps = 0)
    {
        _minFrameIntervalMs = maxFps > 0 ? Math.Max(1, 1000 / maxFps) : 0;
        BackgroundColor = Colors.Black;
        _image = new Image
        {
            Aspect = Aspect.AspectFit,
            BackgroundColor = Colors.Black,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        Content = _image;
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            _image.HandlerChanged += (_, _) => BindNativeImage();
            HandlerChanged += (_, _) => BindNativeImage();
        }
#endif
        if (OperatingSystem.IsAndroid())
            _image.HandlerChanged += (_, _) => BindAndroidImageView();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _width = _height = _stride = 0;
            _pending = [];
        }

        Interlocked.Exchange(ref _dirty, 0);
        Interlocked.Exchange(ref _scheduled, 0);
#if WINDOWS
        UnhookWinRendering();
#endif
        MainThread.BeginInvokeOnMainThread(() =>
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                _bitmap = null;
                if (_nativeImage is not null)
                    _nativeImage.Source = null;
            }
#endif
            if (OperatingSystem.IsAndroid())
            {
                AndroidImagePresenter.Clear(_androidImageView);
                AndroidImagePresenter.Recycle(ref _androidBitmap);
            }

            // 停播时清掉 Source；播放中勿设 Source，以免 MapSource 清掉我们的 Bitmap
            _image.Source = null;
        });
    }

    public void OnVideoFormat(int width, int height, int strideBytes)
    {
        lock (_gate)
        {
            _width = width;
            _height = height;
            _stride = strideBytes;
            var need = Math.Max(0, strideBytes * height);
            if (_pending.Length < need)
                _pending = new byte[need];
        }
    }

    public void OnVideoFrame(IntPtr scan0, int width, int height, int strideBytes)
    {
        if (scan0 == IntPtr.Zero || width <= 0 || height <= 0 || strideBytes <= 0)
            return;

        if (_minFrameIntervalMs > 0)
        {
            var now = Environment.TickCount64;
            if (now - _lastAcceptTick < _minFrameIntervalMs)
                return;
            _lastAcceptTick = now;
        }

        var bytes = checked(strideBytes * height);
        lock (_gate)
        {
            if (_pending.Length < bytes)
                _pending = new byte[bytes];
            Marshal.Copy(scan0, _pending, 0, bytes);
            _width = width;
            _height = height;
            _stride = strideBytes;
        }

        Interlocked.Exchange(ref _dirty, 1);
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            EnsureWinRenderingHooked();
            return;
        }
#endif
        SchedulePresent();
    }

    private void SchedulePresent()
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
            return;
        MainThread.BeginInvokeOnMainThread(PresentOnUi);
    }

    private void PresentOnUi()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
            return;

        byte[] pixels;
        int width, height, stride;
        lock (_gate)
        {
            width = _width;
            height = _height;
            stride = _stride;
            if (width <= 0 || height <= 0 || stride <= 0 || _pending.Length < stride * height)
                return;
            pixels = _pending;
        }

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            PresentWindows(pixels, width, height, stride);
            if (Volatile.Read(ref _dirty) != 0)
                SchedulePresent();
            return;
        }
#endif
        if (OperatingSystem.IsAndroid())
        {
            PresentAndroid(pixels, width, height, stride);
            if (Volatile.Read(ref _dirty) != 0)
                SchedulePresent();
            return;
        }

        PresentPortableBmp(pixels, width, height, stride);
        if (Volatile.Read(ref _dirty) != 0)
            SchedulePresent();
    }

#if WINDOWS
    private void EnsureWinRenderingHooked()
    {
        if (Interlocked.CompareExchange(ref _winHooked, 1, 0) != 0)
            return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_winHooked == 1)
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnWinCompositionRendering;
        });
    }

    private void UnhookWinRendering()
    {
        if (Interlocked.Exchange(ref _winHooked, 0) == 0)
            return;
        MainThread.BeginInvokeOnMainThread(() =>
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnWinCompositionRendering);
    }

    private void OnWinCompositionRendering(object? sender, object e)
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
            return;

        byte[] pixels;
        int width, height, stride;
        lock (_gate)
        {
            width = _width;
            height = _height;
            stride = _stride;
            if (width <= 0 || height <= 0 || stride <= 0 || _pending.Length < stride * height)
                return;
            pixels = _pending;
        }

        PresentWindows(pixels, width, height, stride);
    }
#endif

#if WINDOWS
    private void PresentWindows(byte[] pixels, int width, int height, int stride)
    {
        BindNativeImage();
        if (_nativeImage is null)
        {
            PresentPortableBmp(pixels, width, height, stride);
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(width, height);
            _nativeImage.Source = _bitmap;
        }

        try
        {
            using var stream = _bitmap.PixelBuffer.AsStream();
            stream.Seek(0, SeekOrigin.Begin);
            var dstStride = width * 4;
            if (stride == dstStride)
                stream.Write(pixels, 0, dstStride * height);
            else
            {
                for (var y = 0; y < height; y++)
                    stream.Write(pixels, y * stride, dstStride);
            }

            _bitmap.Invalidate();
        }
        catch
        {
            /* 宿主卸载/像素缓冲失效时忽略 */
        }
    }

    private void BindNativeImage()
    {
        if (_image.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.Image native)
        {
            _nativeImage = native;
            if (_bitmap is not null && !ReferenceEquals(native.Source, _bitmap))
                native.Source = _bitmap;
        }
    }
#endif

    private void PresentAndroid(byte[] pixels, int width, int height, int stride)
    {
        BindAndroidImageView();
        if (_androidImageView is null)
        {
            if (Interlocked.Exchange(ref _androidFailLogged, 1) == 0)
            {
                AppLog.Warn(
                    $"视频墙：Android Image 平台视图未就绪（handler={_image.Handler is not null}, view={_image.Handler?.PlatformView?.GetType().FullName ?? "null"}, {AndroidImagePresenter.DescribeState()}）");
            }

            return;
        }

        var argb = PackBgraToArgbInts(pixels, width, height, stride);
        if (!AndroidImagePresenter.TryPresent(
                _androidImageView, ref _androidBitmap, argb, width, height))
        {
            if (Interlocked.Exchange(ref _androidFailLogged, 1) == 0)
                AppLog.Warn($"视频墙：Android SetPixels 失败（{AndroidImagePresenter.DescribeState()}）");
            return;
        }

        if (Interlocked.Exchange(ref _androidOkLogged, 1) == 0)
        {
            var vw = AndroidImagePresenter.GetViewWidth(_androidImageView);
            var vh = AndroidImagePresenter.GetViewHeight(_androidImageView);
            AppLog.Info($"视频墙：Android ImageView 出画 {width}×{height}（控件 {vw}×{vh}）");
        }
    }

    private void BindAndroidImageView()
    {
        var view = _image.Handler?.PlatformView;
        if (view is null)
            return;
        if (!AndroidImagePresenter.IsImageView(view))
            return;
        _androidImageView = view;
    }

    private int[] PackBgraToArgbInts(byte[] pixels, int width, int height, int stride)
    {
        var n = width * height;
        if (_argbScratch.Length < n)
            _argbScratch = new int[n];
        var dst = _argbScratch;
        for (var y = 0; y < height; y++)
        {
            var srcOff = y * stride;
            var dstOff = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = srcOff + x * 4;
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                dst[dstOff + x] = unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b));
            }
        }

        return dst;
    }

    private void PresentPortableBmp(byte[] pixels, int width, int height, int stride)
    {
        try
        {
            var row = width * 4;
            var need = row * height;
            var packed = new byte[need];
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(pixels, y * stride, packed, y * row, row);
                var off = y * row;
                for (var i = 3; i < row; i += 4)
                    packed[off + i] = 0xFF;
            }

            var bmp = EncodeBgraBmp(packed, width, height, row);
            _image.Source = ImageSource.FromStream(() => new MemoryStream(bmp));
        }
        catch
        {
            /* 忽略单帧失败 */
        }
    }

    private static byte[] EncodeBgraBmp(byte[] pixels, int width, int height, int stride)
    {
        var rowSize = width * 4;
        var pixelBytes = rowSize * height;
        var fileSize = 14 + 40 + pixelBytes;
        var buf = new byte[fileSize];
        buf[0] = (byte)'B';
        buf[1] = (byte)'M';
        BitConverter.TryWriteBytes(buf.AsSpan(2, 4), fileSize);
        BitConverter.TryWriteBytes(buf.AsSpan(10, 4), 14 + 40);
        BitConverter.TryWriteBytes(buf.AsSpan(14, 4), 40);
        BitConverter.TryWriteBytes(buf.AsSpan(18, 4), width);
        BitConverter.TryWriteBytes(buf.AsSpan(22, 4), -height);
        BitConverter.TryWriteBytes(buf.AsSpan(26, 2), (short)1);
        BitConverter.TryWriteBytes(buf.AsSpan(28, 2), (short)32);
        BitConverter.TryWriteBytes(buf.AsSpan(34, 4), pixelBytes);
        var dst = 54;
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * stride, buf, dst, rowSize);
            dst += rowSize;
        }

        return buf;
    }

    /// <summary>写到 MAUI Image 已布局好的 Android.Widget.ImageView。</summary>
    private static class AndroidImagePresenter
    {
        private static readonly object InitGate = new();
        private static bool _resolved;
        private static string _state = "pending";
        private static Type? _bitmapType;
        private static Type? _configType;
        private static Type? _imageViewType;
        private static object? _argb8888;
        private static MethodInfo? _createBitmap;
        private static MethodInfo? _setPixels;
        private static MethodInfo? _recycle;
        private static MethodInfo? _setImageBitmap;
        private static MethodInfo? _invalidate;
        private static MethodInfo? _prepareToDraw;
        private static PropertyInfo? _widthProp;
        private static PropertyInfo? _heightProp;
        private static PropertyInfo? _viewWidthProp;
        private static PropertyInfo? _viewHeightProp;

        public static string DescribeState() => _state;

        public static bool IsImageView(object platformView)
        {
            EnsureResolved();
            if (_imageViewType is not null && _imageViewType.IsInstanceOfType(platformView))
                return true;
            return platformView.GetType().Name.Contains("ImageView", StringComparison.Ordinal);
        }

        public static int GetViewWidth(object imageView)
        {
            try { return Convert.ToInt32(_viewWidthProp?.GetValue(imageView) ?? 0); }
            catch { return 0; }
        }

        public static int GetViewHeight(object imageView)
        {
            try { return Convert.ToInt32(_viewHeightProp?.GetValue(imageView) ?? 0); }
            catch { return 0; }
        }

        public static bool TryPresent(object imageView, ref object? bitmap, int[] argb, int width, int height)
        {
            EnsureResolved();
            if (_createBitmap is null || _setPixels is null || _setImageBitmap is null || _argb8888 is null)
                return false;

            try
            {
                var setBmp = imageView.GetType().GetMethod("SetImageBitmap", BindingFlags.Instance | BindingFlags.Public)
                             ?? _setImageBitmap;
                var invalidate = imageView.GetType().GetMethod("Invalidate", Type.EmptyTypes)
                                 ?? _invalidate;

                var needNew = bitmap is null
                              || (int)(_widthProp!.GetValue(bitmap)!) != width
                              || (int)(_heightProp!.GetValue(bitmap)!) != height;
                if (needNew)
                {
                    var created = _createBitmap.Invoke(null, [width, height, _argb8888]);
                    if (created is null)
                        return false;
                    var old = bitmap;
                    bitmap = created;
                    if (old is not null)
                    {
                        var doomed = old;
                        Recycle(ref doomed);
                    }
                }

                _setPixels.Invoke(bitmap, [argb, 0, width, 0, 0, width, height]);
                try { _prepareToDraw?.Invoke(bitmap, null); }
                catch { /* optional */ }

                // 每帧重新 SetImageBitmap，迫使 Drawable 刷新（仅 Invalidate 在部分机型不够）
                setBmp.Invoke(imageView, [bitmap]);
                invalidate?.Invoke(imageView, null);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"视频墙：Android SetPixels 失败: {ex.GetBaseException().Message}");
                return false;
            }
        }

        public static void Clear(object? imageView)
        {
            if (imageView is null) return;
            EnsureResolved();
            try
            {
                var setBmp = imageView.GetType().GetMethod("SetImageBitmap", BindingFlags.Instance | BindingFlags.Public)
                             ?? _setImageBitmap;
                setBmp?.Invoke(imageView, [null]);
            }
            catch { /* ignore */ }
        }

        public static void Recycle(ref object? bitmap)
        {
            if (bitmap is null) return;
            EnsureResolved();
            try { _recycle?.Invoke(bitmap, null); }
            catch { /* ignore */ }
            bitmap = null;
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            lock (InitGate)
            {
                if (_resolved) return;
                try
                {
                    _bitmapType = FindType("Android.Graphics.Bitmap");
                    _configType = FindType("Android.Graphics.Bitmap+Config")
                                  ?? _bitmapType?.GetNestedType("Config", BindingFlags.Public);
                    _imageViewType = FindType("Android.Widget.ImageView");
                    var viewType = FindType("Android.Views.View");

                    if (_bitmapType is null || _configType is null || _imageViewType is null)
                    {
                        _state =
                            $"types missing bmp={_bitmapType is not null} cfg={_configType is not null} iv={_imageViewType is not null}";
                        return;
                    }

                    _argb8888 = GetJavaEnumValue(_configType, "Argb8888")
                                ?? GetJavaEnumValue(_configType, "ARGB_8888");
                    if (_argb8888 is null)
                    {
                        _state = $"config Argb8888 missing (IsEnum={_configType.IsEnum})";
                        return;
                    }

                    _createBitmap = _bitmapType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m =>
                            m.Name == "CreateBitmap"
                            && m.GetParameters() is { Length: 3 } p
                            && p[0].ParameterType == typeof(int)
                            && p[1].ParameterType == typeof(int)
                            && p[2].ParameterType == _configType);
                    _setPixels = _bitmapType.GetMethod(
                        "SetPixels",
                        [typeof(int[]), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)]);
                    _recycle = _bitmapType.GetMethod("Recycle", Type.EmptyTypes);
                    _prepareToDraw = _bitmapType.GetMethod("PrepareToDraw", Type.EmptyTypes);
                    _widthProp = _bitmapType.GetProperty("Width");
                    _heightProp = _bitmapType.GetProperty("Height");
                    _setImageBitmap = _imageViewType.GetMethod("SetImageBitmap", [_bitmapType]);
                    _invalidate = _imageViewType.GetMethod("Invalidate", Type.EmptyTypes);
                    _viewWidthProp = viewType?.GetProperty("Width") ?? _imageViewType.GetProperty("Width");
                    _viewHeightProp = viewType?.GetProperty("Height") ?? _imageViewType.GetProperty("Height");

                    if (_createBitmap is null || _setPixels is null || _setImageBitmap is null)
                    {
                        _state =
                            $"members missing create={_createBitmap is not null} setPixels={_setPixels is not null} setBmp={_setImageBitmap is not null}";
                        return;
                    }

                    _state = "ok";
                    _resolved = true;
                }
                catch (Exception ex)
                {
                    _state = "init-ex:" + ex.Message;
                    AppLog.Warn($"视频墙：Android 出画反射初始化失败: {ex.Message}");
                }
            }
        }

        private static object? GetJavaEnumValue(Type type, string name)
        {
            try
            {
                if (type.IsEnum)
                    return Enum.Parse(type, name, ignoreCase: true);
            }
            catch { /* fall through */ }

            const BindingFlags staticPublic = BindingFlags.Public | BindingFlags.Static;
            var prop = type.GetProperty(name, staticPublic)
                       ?? type.GetProperties(staticPublic)
                           .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (prop is not null)
                return prop.GetValue(null);

            var field = type.GetField(name, staticPublic)
                        ?? type.GetFields(staticPublic)
                            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            return field?.GetValue(null);
        }

        private static Type? FindType(string fullName)
        {
            var direct = Type.GetType(fullName + ", Mono.Android", throwOnError: false);
            if (direct is not null)
                return direct;

            try
            {
                var mono = Assembly.Load("Mono.Android");
                var t = mono.GetType(fullName, throwOnError: false, ignoreCase: false);
                if (t is not null)
                    return t;
            }
            catch { /* ignore */ }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
                    if (t is not null)
                        return t;
                }
                catch { /* ignore */ }
            }

            return null;
        }
    }
}
