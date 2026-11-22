using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

/// <summary>
/// WPF 单路软件帧面：LibVLC RV32 → WriteableBitmap。
/// 解码线程写后缓冲；由 <see cref="VideoWallFramePresentPump"/> 按刷新率出画。
/// </summary>
public sealed class VideoWallFrameSurface : IVideoFrameSink
{
    private readonly System.Windows.Controls.Image _image;
    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly int _minFrameIntervalMs;
    private byte[] _pending = [];
    private int _width;
    private int _height;
    private int _stride;
    private int _dirty;
    private long _lastAcceptTick;
    private WriteableBitmap? _bitmap;
    private int _frameSignal;
    private int _registered;

    /// <summary>首帧已画到 Image 后触发（Clear 后可再触发）。</summary>
    public event Action? FramePresented;

    /// <param name="maxFps">出画上限；0 表示不限（单路）。宫格建议 12–20。</param>
    public VideoWallFrameSurface(System.Windows.Controls.Image image, int maxFps = 0)
    {
        _image = image;
        _dispatcher = image.Dispatcher;
        _image.Stretch = Stretch.Uniform;
        _minFrameIntervalMs = maxFps > 0 ? Math.Max(1, 1000 / maxFps) : 0;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _width = _height = _stride = 0;
            _pending = [];
        }

        Interlocked.Exchange(ref _dirty, 0);
        Interlocked.Exchange(ref _frameSignal, 0);
        UnregisterPump();
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
        EnsureRegistered();
    }

    internal void PresentIfDirty()
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

    private void EnsureRegistered()
    {
        if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0)
            return;

        if (_dispatcher.CheckAccess())
            VideoWallFramePresentPump.Request(this);
        else
            _ = _dispatcher.BeginInvoke(() => VideoWallFramePresentPump.Request(this), DispatcherPriority.Render);
    }

    private void UnregisterPump()
    {
        if (Interlocked.Exchange(ref _registered, 0) == 0)
            return;

        if (_dispatcher.CheckAccess())
            VideoWallFramePresentPump.Unregister(this);
        else
            _ = _dispatcher.BeginInvoke(() => VideoWallFramePresentPump.Unregister(this), DispatcherPriority.Render);
    }
}
