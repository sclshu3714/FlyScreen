namespace FlyScreen.ToolSdk;

/// <summary>屏幕取色（由宿主平台注入，工具包不引用 WPF/Maui）。</summary>
public interface IScreenColorSampler
{
    bool TrySample(int screenX, int screenY, out RgbaColor color);
}

public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    public static RgbaColor FromRgb(int r, int g, int b)
        => new((byte)r, (byte)g, (byte)b);

    public static RgbaColor FromArgb(int a, int r, int g, int b)
        => new((byte)r, (byte)g, (byte)b, (byte)a);
}

/// <summary>颜色拾取器服务契约（实现位于工具包程序集）。</summary>
public interface IColorPickerService
{
    event Action? SessionChanged;
    event Action? ColorChanged;

    bool IsRunning { get; }
    bool IsPicking { get; }
    RgbaColor Current { get; }
    RgbaColor Committed { get; }
    IScreenColorSampler? Sampler { get; set; }

    void Start();
    void Stop();
    void BeginPicking();
    void CancelPicking();
    void UpdatePreview(RgbaColor color);
    void Commit(RgbaColor color);
    bool TrySampleAt(int screenX, int screenY, out RgbaColor color);
}
