namespace FlyScreen.Services.Tools.ColorPicker;

/// <summary>本机颜色拾取器会话（内嵌工作区；无跨屏）。</summary>
public sealed class ColorPickerService
{
    private readonly object _gate = new();
    private bool _running;
    private bool _picking;
    private RgbaColor _current = new(37, 99, 235);
    private RgbaColor _committed = new(37, 99, 235);
    private IScreenColorSampler? _sampler;

    /// <summary>运行态 / 拾色态变化（供工具卡片刷新，勿用于高频预览）。</summary>
    public event Action? SessionChanged;

    /// <summary>当前颜色变化（含预览；面板轻量刷新）。</summary>
    public event Action? ColorChanged;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public bool IsPicking
    {
        get { lock (_gate) return _picking; }
    }

    public RgbaColor Current
    {
        get { lock (_gate) return _current; }
    }

    public RgbaColor Committed
    {
        get { lock (_gate) return _committed; }
    }

    public IScreenColorSampler? Sampler
    {
        get { lock (_gate) return _sampler; }
        set { lock (_gate) _sampler = value; }
    }

    public void Start()
    {
        lock (_gate)
        {
            _running = true;
            _picking = false;
            _current = _committed;
        }

        RaiseSession();
        RaiseColor();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _picking = false;
            _current = _committed;
        }

        RaiseSession();
        RaiseColor();
    }

    public void BeginPicking()
    {
        lock (_gate)
        {
            if (!_running) return;
            _picking = true;
        }

        RaiseSession();
    }

    public void CancelPicking()
    {
        lock (_gate)
        {
            if (!_picking) return;
            _picking = false;
            _current = _committed;
        }

        RaiseSession();
        RaiseColor();
    }

    public void UpdatePreview(RgbaColor color)
    {
        lock (_gate)
        {
            if (!_picking) return;
            if (_current.R == color.R && _current.G == color.G && _current.B == color.B && _current.A == color.A)
                return;
            _current = color;
        }

        RaiseColor();
    }

    public void Commit(RgbaColor color)
    {
        lock (_gate)
        {
            _current = color;
            _committed = color;
            _picking = false;
        }

        RaiseSession();
        RaiseColor();
    }

    public bool TrySampleAt(int screenX, int screenY, out RgbaColor color)
    {
        var sampler = Sampler;
        if (sampler == null)
        {
            color = default;
            return false;
        }

        return sampler.TrySample(screenX, screenY, out color);
    }

    private void RaiseSession()
    {
        try { SessionChanged?.Invoke(); }
        catch { /* ignore */ }
    }

    private void RaiseColor()
    {
        try { ColorChanged?.Invoke(); }
        catch { /* ignore */ }
    }
}
