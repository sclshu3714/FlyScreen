using System.Runtime.InteropServices;
#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
#endif
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>
/// Maui 软件帧播放面：LibVLC RV32 → Image。
/// Windows 优先 WriteableBitmap；其它宿主（含 Android）写 BMP 供 MAUI Image 显示。
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
        if (OperatingSystem.IsWindows())
        {
            _image.HandlerChanged += (_, _) => BindNativeImage();
            HandlerChanged += (_, _) => BindNativeImage();
        }
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

        if (Interlocked.Exchange(ref _posted, 1) == 0)
            MainThread.BeginInvokeOnMainThread(PresentOnUi);
    }

    private void PresentOnUi()
    {
        Interlocked.Exchange(ref _posted, 0);
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

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            PresentWindows(pixels, width, height, stride);
            return;
        }
#endif
        PresentPortableBmp(pixels, width, height, stride);
    }

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

    /// <summary>跨端兜底：BGRA → 32bpp BMP，无额外依赖。</summary>
    private void PresentPortableBmp(byte[] pixels, int width, int height, int stride)
    {
        try
        {
            var bmp = EncodeBgraBmp(pixels, width, height, stride);
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
        // BITMAPFILEHEADER
        buf[0] = (byte)'B';
        buf[1] = (byte)'M';
        BitConverter.TryWriteBytes(buf.AsSpan(2, 4), fileSize);
        BitConverter.TryWriteBytes(buf.AsSpan(10, 4), 14 + 40);
        // BITMAPINFOHEADER
        BitConverter.TryWriteBytes(buf.AsSpan(14, 4), 40);
        BitConverter.TryWriteBytes(buf.AsSpan(18, 4), width);
        BitConverter.TryWriteBytes(buf.AsSpan(22, 4), -height); // top-down
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
}
