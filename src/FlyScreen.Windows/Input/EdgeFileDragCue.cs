using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using FlyScreen.Native;

namespace FlyScreen.Input;

/// <summary>
/// 目标电脑上跟光标走的拖拽提示：文件名，以及松开后会落到的文件夹。
/// </summary>
public static class EdgeFileDragCue
{
    private const int WmShow = 0x8000 + 51;
    private const int WmHide = 0x8000 + 52;
    private const int WmPaint = 0x000F;
    private const int WmTimer = 0x0113;

    private static readonly object Gate = new();
    private static readonly WndProcDelegate Proc = WndProc;
    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static int _wantVisible;
    private static int _count;
    private static string[] _names = [];
    private static Func<string?>? _resolve;
    private static string _place = "";
    private static long _resolvedAt;

    public static void Show(int count, IReadOnlyList<string> names, Func<string?> resolveDropDirectory)
    {
        lock (Gate)
        {
            _count = Math.Max(count, names.Count);
            _names = names.Where(n => !string.IsNullOrWhiteSpace(n)).Take(4).ToArray();
            _resolve = resolveDropDirectory;
            _resolvedAt = 0;
        }

        Interlocked.Exchange(ref _wantVisible, 1);
        EnsureThread();
        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WmShow, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Hide()
    {
        Interlocked.Exchange(ref _wantVisible, 0);
        lock (Gate)
        {
            _count = 0;
            _names = [];
            _resolve = null;
            _place = "";
        }

        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WmHide, IntPtr.Zero, IntPtr.Zero);
    }

    private static void EnsureThread()
    {
        lock (Gate)
        {
            if (_thread != null) return;
            _thread = new Thread(ThreadMain) { IsBackground = true, Name = "FlyScreen.EdgeDragCue" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private static void ThreadMain()
    {
        var hInstance = NativeMethods.GetModuleHandle(null);
        var cls = new WndClass
        {
            lpfnWndProc = Proc,
            hInstance = hInstance,
            lpszClassName = "FlyScreenEdgeDragCue",
            hbrBackground = new IntPtr(6)
        };
        RegisterClass(ref cls);
        _hwnd = CreateWindowEx(
            0x00000008 | 0x00000080 | 0x08000000 | 0x00000020 | 0x00080000,
            cls.lpszClassName, "",
            unchecked((int)0x80000000),
            0, 0, 220, 72,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;
        SetLayeredWindowAttributes(_hwnd, 0, 242, 2);
        SetTimer(_hwnd, 1, 33, IntPtr.Zero);
        if (Volatile.Read(ref _wantVisible) == 1)
            ShowWindow(_hwnd, 8);

        var msg = new Msg();
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmShow && Volatile.Read(ref _wantVisible) == 1)
            {
                RefreshPlace(force: true);
                FitToText();
                ShowWindow(_hwnd, 8);
                MoveNearCursor();
            }
            else if (msg.message == WmHide || (msg.message == WmShow && Volatile.Read(ref _wantVisible) == 0))
            {
                ShowWindow(_hwnd, 0);
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTimer && Volatile.Read(ref _wantVisible) == 1)
        {
            RefreshPlace(force: false);
            MoveNearCursor();
            return IntPtr.Zero;
        }

        if (msg == WmPaint)
        {
            Paint(hwnd);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void RefreshPlace(bool force)
    {
        var now = Environment.TickCount64;
        long last;
        Func<string?>? resolve;
        lock (Gate)
        {
            last = _resolvedAt;
            resolve = _resolve;
        }

        if (!force && now - last < 150) return;
        string place;
        try
        {
            place = Friendly(resolve?.Invoke());
        }
        catch
        {
            place = "";
        }

        var changed = false;
        lock (Gate)
        {
            _resolvedAt = now;
            if (!string.Equals(_place, place, StringComparison.Ordinal))
            {
                _place = place;
                changed = true;
            }
        }

        if (changed && _hwnd != IntPtr.Zero)
        {
            FitToText();
            InvalidateRect(_hwnd, IntPtr.Zero, true);
        }
    }

    private static void MoveNearCursor()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var rect)) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        var w = Math.Max(40, rect.Right - rect.Left);
        var h = Math.Max(24, rect.Bottom - rect.Top);
        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        var vh = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        var x = pt.X + 18;
        var y = pt.Y + 22;
        if (x + w > vx + vw) x = pt.X - w - 12;
        if (y + h > vy + vh) y = pt.Y - h - 12;
        x = Math.Clamp(x, vx, vx + vw - w);
        y = Math.Clamp(y, vy, vy + vh - h);
        SetWindowPos(_hwnd, new IntPtr(-1), x, y, w, h, 0x0010 | 0x0001);
    }

    private static void FitToText()
    {
        if (_hwnd == IntPtr.Zero) return;
        var lines = Lines();
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var font = CreateFont(bold: false);
        using var fontBold = CreateFont(bold: true);
        float width = 160;
        float height = 16;
        for (var i = 0; i < lines.Count; i++)
        {
            var size = g.MeasureString(lines[i], i == 0 ? fontBold : font);
            width = Math.Max(width, size.Width + 18);
            height += size.Height + 2;
        }

        var dpi = 96;
        try { dpi = Math.Max(96, (int)GetDpiForSystem()); } catch { /* Win10 以下没有 */ }
        var pad = 12 * dpi / 96;
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, (int)width + pad * 2, (int)height + pad, 0x0010 | 0x0002);
    }

    private static void Paint(IntPtr hwnd)
    {
        var ps = new PaintStruct { rgb = new byte[32] };
        var hdc = BeginPaint(hwnd, ref ps);
        try
        {
            GetClientRect(hwnd, out var rc);
            using var g = Graphics.FromHdc(hdc);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var back = new SolidBrush(Color.FromArgb(248, 250, 252));
            using var border = new Pen(Color.FromArgb(180, 190, 200));
            g.FillRectangle(back, 0, 0, rc.Right, rc.Bottom);
            g.DrawRectangle(border, 0, 0, Math.Max(1, rc.Right - 1), Math.Max(1, rc.Bottom - 1));
            DrawFileIcon(g, 8, 8);
            var lines = Lines();
            using var font = CreateFont(bold: false);
            using var fontBold = CreateFont(bold: true);
            using var text = new SolidBrush(Color.FromArgb(32, 36, 40));
            using var muted = new SolidBrush(Color.FromArgb(70, 90, 110));
            float y = 8;
            for (var i = 0; i < lines.Count; i++)
            {
                var f = i == 0 ? fontBold : font;
                var brush = i == 0 ? text : muted;
                g.DrawString(lines[i], f, brush, 28, y);
                y += g.MeasureString(lines[i], f).Height + 1;
            }
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private static List<string> Lines()
    {
        int count;
        string[] names;
        string place;
        lock (Gate)
        {
            count = _count;
            names = _names;
            place = _place;
        }

        var lines = new List<string>
        {
            string.IsNullOrWhiteSpace(place) ? "松开取消" : $"复制到 {place}"
        };
        lines.AddRange(names);
        if (count > names.Length && names.Length > 0)
            lines.Add($"以及其他 {count - names.Length} 项");
        else if (names.Length == 0)
            lines.Add(count > 1 ? $"{count} 项" : "1 项");
        return lines;
    }

    private static void DrawFileIcon(Graphics g, int x, int y)
    {
        using var page = new SolidBrush(Color.FromArgb(37, 99, 235));
        using var fold = new SolidBrush(Color.FromArgb(191, 219, 254));
        g.FillRectangle(page, x, y + 3, 14, 16);
        g.FillPolygon(fold, [new PointF(x + 7, y + 3), new PointF(x + 14, y + 9), new PointF(x + 7, y + 9)]);
    }

    private static string Friendly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var full = Path.GetFullPath(path);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktop)
                && string.Equals(full, Path.GetFullPath(desktop), StringComparison.OrdinalIgnoreCase))
                return "桌面";
            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? full : name;
        }
        catch
        {
            return path;
        }
    }

    private static Font CreateFont(bool bold)
    {
        var dpi = 96;
        try { dpi = Math.Max(96, (int)GetDpiForSystem()); } catch { /* ignore */ }
        var size = 9f * dpi / 96f;
        var style = bold ? FontStyle.Bold : FontStyle.Regular;
        try
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
        }
        catch
        {
            return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr hdc;
        public int erase;
        public Rect rc;
        public int restore;
        public int incUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgb;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, ref PaintStruct ps);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
