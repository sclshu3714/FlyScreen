namespace FlyScreen.Services.ScreenExtend;

/// <summary>
/// 黑边裁切一旦成立就保持，避免弹幕扫过黑边时「裁了 / 没裁」来回跳。
/// 连续多帧确认没有黑边，或画面尺寸变了，才放开。
/// </summary>
public sealed class ProjectionCropHold
{
    /// <summary>连续这么多帧测到整幅画面，才取消裁切。</summary>
    public const int ReleaseFrames = 12;

    /// <summary>裁切区域明显不同时，连续这么多帧才改口。</summary>
    public const int SwitchFrames = 4;

    private int _width;
    private int _height;
    private bool _holding;
    private ProjectionBlackBorder _held;
    private ProjectionBlackBorder _pending;
    private int _release;
    private int _switch;

    public void Reset()
    {
        _width = 0;
        _height = 0;
        _holding = false;
        _release = 0;
        _switch = 0;
        _pending = default;
        _held = default;
    }

    public ProjectionBlackBorder Stabilize(int frameW, int frameH, ProjectionBlackBorder measured)
    {
        if (frameW != _width || frameH != _height)
        {
            _width = frameW;
            _height = frameH;
            _holding = false;
            _release = 0;
            _switch = 0;
        }

        if (frameW < 32 || frameH < 32)
            return measured;

        if (!_holding)
        {
            if (measured.IsFullFrame(frameW, frameH))
                return measured;
            Lock(measured);
            return _held;
        }

        if (Similar(_held, measured, frameW, frameH))
        {
            _release = 0;
            _switch = 0;
            return _held;
        }

        if (measured.IsFullFrame(frameW, frameH))
        {
            _switch = 0;
            _release++;
            if (_release >= ReleaseFrames)
            {
                _holding = false;
                _release = 0;
                return measured;
            }

            return _held;
        }

        _release = 0;
        if (_switch > 0 && Similar(_pending, measured, frameW, frameH))
            _switch++;
        else
        {
            _pending = measured;
            _switch = 1;
        }

        if (_switch >= SwitchFrames)
            Lock(measured);
        return _held;
    }

    private void Lock(ProjectionBlackBorder crop)
    {
        _holding = true;
        _held = crop;
        _release = 0;
        _switch = 0;
        _pending = default;
    }

    private static bool Similar(ProjectionBlackBorder a, ProjectionBlackBorder b, int frameW, int frameH)
    {
        var tolX = Math.Max(8, frameW / 25);
        var tolY = Math.Max(8, frameH / 25);
        return Math.Abs(a.Left - b.Left) <= tolX
               && Math.Abs(a.Top - b.Top) <= tolY
               && Math.Abs(a.Left + a.Width - (b.Left + b.Width)) <= tolX
               && Math.Abs(a.Top + a.Height - (b.Top + b.Height)) <= tolY;
    }
}
