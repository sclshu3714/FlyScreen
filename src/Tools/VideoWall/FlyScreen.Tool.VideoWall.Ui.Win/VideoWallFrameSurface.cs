using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

/// <summary>
/// WPF 单路软件帧面：LibVLC RV32 → WriteableBitmap。
/// 画面与 chrome 同属 WPF 树，无 VideoView ForegroundWindow。
/// </summary>
public sealed class VideoWallFrameSurface : IVideoFrameSink
{
    private readonly System.Windows.Controls.Image _image;
    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private byte[] _copy = [];
    private int _width;
    private int _height;
    private int _stride;
    private int _posted;
    private WriteableBitmap? _bitmap;
    private int _frameSignal;

    /// <summary>首帧已画到 Image 后触发（Clear 后可再触发）。</summary>
    public event Action? FramePresented;

    public VideoWallFrameSurface(System.Windows.Controls.Image image)
    {
        _image = image;
        _dispatcher = image.Dispatcher;
        _image.Stretch = Stretch.Uniform;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _width = _height = _stride = 0;
            _copy = [];
        }

        Interlocked.Exchange(ref _posted, 0);
        Interlocked.Exchange(ref _frameSignal, 0);
        _ = _dispatcher.BeginInvoke(() =>
        {
            _bitmap = null;
            _image.Source = null;
        }, DispatcherPriority.Render);
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
            _ = _dispatcher.BeginInvoke(PresentOnUi, DispatcherPriority.Render);
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
            if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                _image.Source = _bitmap;
            }

            var rect = new Int32Rect(0, 0, width, height);
            _bitmap.WritePixels(rect, pixels, stride, 0);
            if (Interlocked.Exchange(ref _frameSignal, 1) == 0)
                FramePresented?.Invoke();
        }
        catch
        {
            /* 卸载过程中忽略 */
        }
    }
}
