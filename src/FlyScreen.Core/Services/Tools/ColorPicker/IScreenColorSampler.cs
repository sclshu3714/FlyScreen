namespace FlyScreen.Services.Tools.ColorPicker;

/// <summary>按屏幕坐标采样像素；由各桌面端实现（Core 无 Win32）。</summary>
public interface IScreenColorSampler
{
    bool TrySample(int screenX, int screenY, out RgbaColor color);
}
