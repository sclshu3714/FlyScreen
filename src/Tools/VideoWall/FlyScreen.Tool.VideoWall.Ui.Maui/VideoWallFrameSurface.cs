using System.Runtime.InteropServices;
#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
#endif
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>
/// Maui 单路完整播放面：LibVLC RV32 → Image，与 chrome 同树（无顶层浮层）。
/// </summary>
public sealed class VideoWallFrameSurface : ContentView, IVideoFrameSink
{
    private readonly Image _image;
    private readonly object _gate = new();
    private byte[] _copy = [];
    private int _width;
    private int _height;
    private int _stride;
    private int _posted;

#if WINDOWS
    private Microsoft.UI.Xaml.Controls.Image? _nativeImage;
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _bitmap;
#endif

    public VideoWallFrameSurface()
    {
        _image = new Image
        {
            Aspect = Aspect.AspectFit,
            BackgroundColor = Colors.Black,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        Content = _image;
        BackgroundColor = Colors.Black;
#if WINDOWS
        _image.HandlerChanged += (_, _) => BindNativeImage();
        HandlerChanged += (_, _) => BindNativeImage();
#endif
    }

    public void Clear()
    {
        lock (_gate)
        {
            _width = _height = _stride = 0;
            _copy = [];
        }

        Interlocked.Exchange(ref _posted, 0);
#if WINDOWS
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _bitmap = null;
            if (_nativeImage is not null)
                _nativeImage.Source = null;
            _image.Source = null;
        });
#endif
    }

    public void OnVideoFormat(int width, int height, int strideBytes)
    {
        lock (_gate)
        {
            _width = width;
            _height = height;
            _stride = strideBytes;
            var need = Math.Max(0, strideBytes * height);
            if (_copy.Length < need)
                _copy = new byte[need];
        }
    }

    public void OnVideoFrame(IntPtr scan0, int width, int height, int strideBytes)
    {
        if (scan0 == IntPtr.Zero || width <= 0 || height <= 0 || strideBytes <= 0)
            return;

        var bytes = checked(strideBytes * height);
        lock (_gate)
        {
            if (_copy.Length < bytes)
                _copy = new byte[bytes];
            Marshal.Copy(scan0, _copy, 0, bytes);
            _width = width;
            _height = height;
            _stride = strideBytes;
        }

        // 合并脏帧：UI 忙时丢中间帧，避免队列堆积
        if (Interlocked.Exchange(ref _posted, 1) == 0)
            MainThread.BeginInvokeOnMainThread(PresentOnUi);
    }

    private void PresentOnUi()
    {
        Interlocked.Exchange(ref _posted, 0);
#if WINDOWS
        byte[] pixels;
        int width, height, stride;
        lock (_gate)
        {
            width = _width;
            height = _height;
            stride = _stride;
            if (width <= 0 || height <= 0 || stride <= 0 || _copy.Length < stride * height)
                return;
            pixels = _copy;
        }

        BindNativeImage();
        if (_nativeImage is null)
            return;

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
            {
                stream.Write(pixels, 0, dstStride * height);
            }
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
#else
        _ = this;
#endif
    }

#if WINDOWS
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
}
