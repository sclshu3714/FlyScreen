using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Ui.Desktop;

/// <summary>
/// Avalonia 单路软件帧面：LibVLC RV32 → WriteableBitmap。
/// 画面与 chrome 同树，无原生顶层浮层。
/// </summary>
public sealed class VideoWallFrameSurface : IVideoFrameSink
{
    private readonly Image _image;
    private readonly object _gate = new();
    private byte[] _copy = [];
    private int _width;
    private int _height;
    private int _stride;
    private int _posted;
    private WriteableBitmap? _bitmap;

    public VideoWallFrameSurface(Image image) => _image = image;

    public void Clear()
    {
        lock (_gate)
        {
            _width = _height = _stride = 0;
            _copy = [];
        }

        Interlocked.Exchange(ref _posted, 0);
        Dispatcher.UIThread.Post(() =>
        {
            _bitmap = null;
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
            Dispatcher.UIThread.Post(PresentOnUi);
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

        try
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                _bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);
                _image.Source = _bitmap;
            }

            using var locked = _bitmap.Lock();
            var dstStride = locked.RowBytes;
            var dst = locked.Address;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(pixels, y * stride, dst + y * dstStride, Math.Min(stride, dstStride));
            }
        }
        catch
        {
            /* 卸载过程中忽略 */
        }
    }
}
