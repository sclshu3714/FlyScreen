namespace FlyScreen.Native;

/// <summary>
/// 虚拟桌面（多显示器并集）度量。边缘鼠标必须用此坐标系：
/// 钩子给出的是虚拟坐标；若误用 SM_CXSCREEN（主屏），副屏整块会被当成「右/左边缘」误切换。
/// </summary>
public static class VirtualScreen
{
    public static void GetBounds(out int left, out int top, out int width, out int height)
    {
        left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (width > 0 && height > 0)
            return;

        left = 0;
        top = 0;
        width = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN));
        height = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN));
    }

    public static void GetCenter(out int cx, out int cy)
    {
        GetBounds(out var left, out var top, out var width, out var height);
        cx = left + width / 2;
        cy = top + height / 2;
    }
}
