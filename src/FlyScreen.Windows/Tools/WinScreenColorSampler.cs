using System.Runtime.InteropServices;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tools;

/// <summary>Win32 GetPixel 屏幕采样（全桌面；避免每帧 CopyFromScreen）。</summary>
public sealed class WinScreenColorSampler : IScreenColorSampler
{
    public bool TrySample(int screenX, int screenY, out RgbaColor color)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            color = default;
            return false;
        }

        try
        {
            var pixel = GetPixel(hdc, screenX, screenY);
            if (pixel == unchecked((uint)-1))
            {
                color = default;
                return false;
            }

            // COLORREF: 0x00BBGGRR
            var r = (byte)(pixel & 0xFF);
            var g = (byte)((pixel >> 8) & 0xFF);
            var b = (byte)((pixel >> 16) & 0xFF);
            color = new RgbaColor(r, g, b);
            return true;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);
}
