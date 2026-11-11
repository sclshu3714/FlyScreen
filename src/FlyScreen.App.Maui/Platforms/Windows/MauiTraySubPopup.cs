#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>托盘「切换机器」二级菜单（分层窗）。</summary>
internal sealed class MauiTraySubPopup : IDisposable
{
    private const string WndClassName = "FlyScreenMauiTraySwitchSub";
    private const int CardMargin = 10;
    private const int CardMinWidth = 168;
    private const int ItemHeight = 36;
    private const int ItemHPad = 14;
    private const int ItemMargin = 4;
    private const int PanelPadX = 4;
    private const int PanelPadY = 6;
    private const int CornerRadius = 12;
    private const int ItemCorner = 8;
    private const int IconSize = 16;
    private const int IconGap = 8;

    private static readonly Color CardBg = Color.FromArgb(255, 0xFB, 0xFD, 0xFF);
    private static readonly Color CardBorder = Color.FromArgb(255, 0xC9, 0xD7, 0xE8);
    private static readonly Color SepColor = Color.FromArgb(255, 0xD5, 0xE0, 0xEC);
    private static readonly Color TextColor = Color.FromArgb(255, 0x11, 0x18, 0x27);
    private static readonly Color HoverBg = Color.FromArgb(255, 0xE8, 0xF2, 0xFF);
    private static readonly Color HoverBorder = Color.FromArgb(255, 0xA9, 0xC3, 0xDE);
    private static readonly Color Accent = Color.FromArgb(255, 0x25, 0x63, 0xEB);
    private static readonly Color Shadow = Color.FromArgb(70, 0x14, 0x23, 0x3A);

    private readonly IReadOnlyList<(string Label, Action Action, byte[]? IconPng)> _items;
    private readonly Action _onItemInvoked;
    private readonly Action? _onDismissRequested;
    private readonly Rectangle[] _itemRects;
    private readonly bool _sepBeforeLast;
    private readonly bool _showIcons;

    private IntPtr _hwnd;
    private WndProc? _proc;
    private int _hoverIndex = -1;
    private bool _invoking;
    private int _width;
    private int _height;
    private int _cardW;
    private int _cardH;

    public bool IsVisible => _hwnd != IntPtr.Zero;
    public IntPtr Hwnd => _hwnd;

    private MauiTraySubPopup(
        IReadOnlyList<(string Label, Action Action, byte[]? IconPng)> items,
        Action onItemInvoked,
        Action? onDismissRequested,
        bool sepBeforeLast)
    {
        _items = items;
        _onItemInvoked = onItemInvoked;
        _onDismissRequested = onDismissRequested;
        _sepBeforeLast = sepBeforeLast && items.Count > 1;
        _showIcons = items.Any(x => x.IconPng is { Length: > 0 });
        _itemRects = new Rectangle[items.Count];
        Measure();
    }

    public static MauiTraySubPopup Show(
        int screenX,
        int screenY,
        IReadOnlyList<(string Label, Action Action, byte[]? IconPng)> items,
        Action onItemInvoked,
        Action? onDismissRequested = null,
        bool sepBeforeLast = true)
    {
        var popup = new MauiTraySubPopup(items, onItemInvoked, onDismissRequested, sepBeforeLast);
        popup.ShowAt(screenX, screenY);
        return popup;
    }

    private void Measure()
    {
        using var font = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        var iconSlot = _showIcons ? IconSize + IconGap : 0;
        var textW = CardMinWidth - PanelPadX * 2 - ItemMargin * 2 - ItemHPad * 2 - iconSlot;
        using (var tmp = new Bitmap(8, 8))
        using (var g = Graphics.FromImage(tmp))
        {
            foreach (var (label, _, _) in _items)
                textW = Math.Max(textW, (int)Math.Ceiling(g.MeasureString(label, font).Width));
        }

        _cardW = Math.Max(CardMinWidth, textW + iconSlot + PanelPadX * 2 + ItemMargin * 2 + ItemHPad * 2);
        var sepBlock = _sepBeforeLast ? 1 + 6 * 2 : 0;
        _cardH = PanelPadY * 2
                 + _items.Count * (ItemHeight + ItemMargin * 2)
                 + sepBlock;
        _width = _cardW + CardMargin * 2;
        _height = _cardH + CardMargin * 2;

        var y = CardMargin + PanelPadY;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_sepBeforeLast && i == _items.Count - 1)
                y += 6 + 1 + 6;
            _itemRects[i] = new Rectangle(
                CardMargin + PanelPadX + ItemMargin,
                y + ItemMargin,
                _cardW - PanelPadX * 2 - ItemMargin * 2,
                ItemHeight);
            y += ItemHeight + ItemMargin * 2;
        }
    }

    private void ShowAt(int screenX, int screenY)
    {
        EnsureWndClass();
        _proc = WndProcImpl;
        var hInstance = GetModuleHandle(null);
        var scale = GetDpiScaleAt(screenX, screenY);
        var widthPx = Math.Max(1, (int)Math.Ceiling(_width * scale));
        var heightPx = Math.Max(1, (int)Math.Ceiling(_height * scale));
        var wa = GetWorkAreaNear(screenX, screenY);
        var left = screenX;
        var top = screenY;
        if (left + widthPx > wa.Right - 4)
            left = Math.Max(wa.Left + 4, screenX - widthPx - 8);
        if (top + heightPx > wa.Bottom - 4)
            top = Math.Max(wa.Top + 4, wa.Bottom - heightPx - 4);

        _hwnd = CreateWindowEx(
            WsExLayered | WsExToolWindow | WsExTopmost,
            WndClassName,
            "",
            WsPopup,
            left, top, widthPx, heightPx,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx tray submenu failed");

        var handle = GCHandle.Alloc(this);
        SetWindowLongPtr(_hwnd, GwlpUserData, GCHandle.ToIntPtr(handle));
        Render(scale);
        ShowWindow(_hwnd, SwShowNoActivate);
    }

    private void Render(double scale)
    {
        var bmpW = Math.Max(1, (int)Math.Ceiling(_width * scale));
        var bmpH = Math.Max(1, (int)Math.Ceiling(_height * scale));
        using var bmp = new Bitmap(bmpW, bmpH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform((float)scale, (float)scale);

            using (var shadowPath = RoundedRect(CardMargin + 1, CardMargin + 3, _cardW, _cardH, CornerRadius))
            using (var shadowBrush = new SolidBrush(Shadow))
                g.FillPath(shadowBrush, shadowPath);

            using (var cardPath = RoundedRect(CardMargin, CardMargin, _cardW, _cardH, CornerRadius))
            using (var bg = new SolidBrush(CardBg))
            using (var pen = new Pen(CardBorder))
            {
                g.FillPath(bg, cardPath);
                g.DrawPath(pen, cardPath);
            }

            using var font = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(TextColor);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };

            if (_sepBeforeLast && _items.Count >= 2)
            {
                var sepY = _itemRects[_items.Count - 2].Bottom + ItemMargin + 6;
                using var sepPen = new Pen(SepColor);
                g.DrawLine(sepPen, CardMargin + 12, sepY, CardMargin + _cardW - 12, sepY);
            }

            for (var i = 0; i < _items.Count; i++)
            {
                var rect = _itemRects[i];
                if (i == _hoverIndex)
                {
                    using var hoverPath = RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, ItemCorner);
                    using var hb = new SolidBrush(HoverBg);
                    using var hp = new Pen(HoverBorder);
                    g.FillPath(hb, hoverPath);
                    g.DrawPath(hp, hoverPath);
                    using var accentBrush = new SolidBrush(Accent);
                    g.FillRectangle(accentBrush, rect.X - 2, rect.Y + (ItemHeight - 20) / 2, 3, 20);
                }

                var x = rect.X + ItemHPad;
                if (_showIcons)
                {
                    var iconPng = _items[i].IconPng;
                    if (iconPng is { Length: > 0 })
                    {
                        try
                        {
                            using var ms = new MemoryStream(iconPng);
                            using var icon = System.Drawing.Image.FromStream(ms);
                            var iy = rect.Y + (rect.Height - IconSize) / 2;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(icon, x, iy, IconSize, IconSize);
                        }
                        catch
                        {
                            /* ignore bad icon */
                        }
                    }

                    x += IconSize + IconGap;
                }

                var textRect = new RectangleF(x, rect.Y, Math.Max(8, rect.Right - ItemHPad - x), rect.Height);
                g.DrawString(_items[i].Label, font, textBrush, textRect, sf);
            }
        }

        UpdateLayeredFromBitmap(bmp);
    }

    private void UpdateLayeredFromBitmap(Bitmap bmp)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);
            var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
            var pointSource = new POINT { X = 0, Y = 0 };
            GetWindowRect(_hwnd, out var wnd);
            var topPos = new POINT { X = wnd.Left, Y = wnd.Top };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            UpdateLayeredWindow(_hwnd, screenDc, ref topPos, ref size, memDc, ref pointSource,
                0, ref blend, UlwAlpha);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmMouseMove:
            {
                var x = (short)(lParam.ToInt64() & 0xFFFF);
                var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                var scale = GetDpiScale();
                var idx = HitTest((int)(x / scale), (int)(y / scale));
                if (idx != _hoverIndex)
                {
                    _hoverIndex = idx;
                    Render(scale);
                }

                TrackMouseLeave(hWnd);
                return IntPtr.Zero;
            }
            case WmMouseLeave:
                if (_hoverIndex >= 0)
                {
                    _hoverIndex = -1;
                    Render(GetDpiScale());
                }

                return IntPtr.Zero;
            case WmLButtonUp:
            {
                var x = (short)(lParam.ToInt64() & 0xFFFF);
                var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                var scale = GetDpiScale();
                var idx = HitTest((int)(x / scale), (int)(y / scale));
                if (idx >= 0 && idx < _items.Count)
                {
                    var act = _items[idx].Action;
                    _invoking = true;
                    // 先执行切换，再关菜单；避免先 Destroy 导致动作被吃掉
                    try { act(); }
                    catch { /* ignore */ }
                    try { _onItemInvoked(); }
                    catch { /* ignore */ }
                }

                return IntPtr.Zero;
            }
            case WmActivate:
                // 点到桌面/其它窗时收起；点本菜单项时由 LButtonUp 处理
                if ((wParam.ToInt64() & 0xFFFF) == 0 && !_invoking)
                    _onDismissRequested?.Invoke();
                return IntPtr.Zero;
            case WmNcDestroy:
            {
                var ud = GetWindowLongPtr(hWnd, GwlpUserData);
                if (ud != IntPtr.Zero)
                {
                    var gh = GCHandle.FromIntPtr(ud);
                    if (gh.IsAllocated) gh.Free();
                    SetWindowLongPtr(hWnd, GwlpUserData, IntPtr.Zero);
                }

                break;
            }
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private int HitTest(int x, int y)
    {
        for (var i = 0; i < _itemRects.Length; i++)
        {
            if (_itemRects[i].Contains(x, y))
                return i;
        }

        return -1;
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        var h = _hwnd;
        _hwnd = IntPtr.Zero;
        try { DestroyWindow(h); } catch { /* ignore */ }
    }

    private double GetDpiScale()
    {
        try
        {
            var dpi = GetDpiForWindow(_hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    private static double GetDpiScaleAt(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
            if (monitor != IntPtr.Zero
                && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
                && dpiX > 0)
                return dpiX / 96.0;
        }
        catch { /* 96dpi */ }

        return 1;
    }

    private void TrackMouseLeave(IntPtr hWnd)
    {
        var tme = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TmeLeave,
            hwndTrack = hWnd,
            dwHoverTime = 0
        };
        TrackMouseEvent(ref tme);
    }

    private static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var d = r * 2;
        var path = new GraphicsPath();
        if (r <= 0)
        {
            path.AddRectangle(new Rectangle(x, y, w, h));
            return path;
        }

        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static bool _classRegistered;
    private static readonly WndProc StaticWndProc = static (h, m, w, l) =>
    {
        var ud = GetWindowLongPtr(h, GwlpUserData);
        if (ud != IntPtr.Zero)
        {
            var gh = GCHandle.FromIntPtr(ud);
            if (gh.Target is MauiTraySubPopup popup && popup._proc != null)
                return popup._proc(h, m, w, l);
        }

        return DefWindowProc(h, m, w, l);
    };

    private static void EnsureWndClass()
    {
        if (_classRegistered) return;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticWndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = WndClassName,
            hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512)
        };
        RegisterClassEx(ref wc);
        _classRegistered = true;
    }

    private static RECT GetWorkAreaNear(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return info.rcWork;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int GwlpUserData = -21;
    private const int SwShowNoActivate = 4;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseLeave = 0x02A3;
    private const uint WmActivate = 0x0006;
    private const uint WmNcDestroy = 0x0082;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const uint UlwAlpha = 0x00000002;
    private const uint TmeLeave = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize, dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
#endif
