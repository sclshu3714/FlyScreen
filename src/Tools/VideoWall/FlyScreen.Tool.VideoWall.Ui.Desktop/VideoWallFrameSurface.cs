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
/// 解码线程写后缓冲；按 Render 优先级节流出画，可选限帧减轻宫格负载。
/// </summary>
public sealed class VideoWallFrameSurface : IVideoFrameSink
{
    private readonly Image _image;
    private readonly object _gate = new();
    private readonly int _minFrameIntervalMs;
    private byte[] _pending = [];
    private int _width;
    private int _height;
    private int _stride;
    private int _dirty;
    private int _scheduled;
    private long _lastAcceptTick;
    private WriteableBitmap? _bitmap;
    private int _frameSignal;

    /// <summary>首帧已画到 Image 后触发（Clear 后可再触发）。</summary>
    public event Action? FramePresented;

    /// <param name="maxFps">出画上限；0 表示不限（单路）。</param>
    public VideoWallFrameSurface(Image image, int maxFps = 0)
    {
        _image = image;
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
        Interlocked.Exchange(ref _scheduled, 0);
        Interlocked.Exchange(ref _frameSignal, 0);
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
        SchedulePresent();
    }

    private void SchedulePresent()
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
            return;

        Dispatcher.UIThread.Post(PresentOnUi, DispatcherPriority.Render);
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
                Marshal.Copy(pixels, y * stride, dst + y * dstStride, Math.Min(stride, dstStride));

            if (Interlocked.Exchange(ref _frameSignal, 1) == 0)
                FramePresented?.Invoke();
        }
        catch
        {
            /* 卸载过程中忽略 */
        }

        if (Volatile.Read(ref _dirty) != 0)
            SchedulePresent();
    }
}
