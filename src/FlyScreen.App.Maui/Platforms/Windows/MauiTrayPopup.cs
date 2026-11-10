#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// Maui-Win 托盘菜单：纯 Win32 <c>UpdateLayeredWindow</c> 逐像素透明
/// （对齐 WPF 圆角卡片；不用 WinForms / 不用 WinUI 色键）。
/// </summary>
internal sealed class MauiTrayPopup : IDisposable
{
    private static MauiTrayPopup? _open;

    private const string WndClassName = "FlyScreenMauiTrayMenuLayered";
    private const int CardMargin = 14;       // 四周等距透明边（留给阴影）
    private const int CardMinWidth = 188;
    private const int ItemHeight = 36;
    private const int ItemHPad = 14;
    private const int ItemMargin = 4;
    private const int PanelPadX = 4;
    private const int PanelPadY = 6;
    private const int CornerRadius = 12;
    private const int ItemCorner = 8;

    private static readonly Color CardBg = Color.FromArgb(255, 0xFB, 0xFD, 0xFF);
    private static readonly Color CardBorder = Color.FromArgb(255, 0xC9, 0xD7, 0xE8);
    private static readonly Color SepColor = Color.FromArgb(255, 0xD5, 0xE0, 0xEC);
    private static readonly Color TextColor = Color.FromArgb(255, 0x11, 0x18, 0x27);
    private static readonly Color HoverBg = Color.FromArgb(255, 0xE8, 0xF2, 0xFF);
    private static readonly Color HoverBorder = Color.FromArgb(255, 0xA9, 0xC3, 0xDE);
    private static readonly Color Accent = Color.FromArgb(255, 0x25, 0x63, 0xEB);
    private static readonly Color Shadow = Color.FromArgb(70, 0x14, 0x23, 0x3A);

    private readonly ProjectionUiPhase _projectPhase;
    private readonly List<TrayRow> _rows;
    private readonly List<(string Label, Action Action)> _subItems;
    private readonly Rectangle[] _itemRects; // 相对窗口客户区（含 CardMargin）；与可点击项一一对应
    private readonly List<int> _sepAfterClickIndex; // 在该可点击项之后画分隔线
    private int _switchClickIndex = -1;

    private IntPtr _hwnd;
    private WndProc? _proc;
    private int _hoverIndex = -1;
    private bool _closing;
    private int _dismissGen;
    private int _width;
    private int _height;
    private int _cardW;
    private int _cardH;
    private MauiTraySubPopup? _subPopup;

    private sealed class TrayRow
    {
        public required string Label { get; init; }
        public Action? Action { get; init; }
        public bool IsHeader { get; init; }
        public bool IsProject { get; init; }
        public bool IsSubmenu { get; init; }
        public bool Clickable => (Action != null || IsSubmenu) && !IsHeader;
    }

    private MauiTrayPopup(
        Action onOpen,
        Action onReconnect,
        Action onProject,
        ProjectionUiPhase projectPhase,
        Action onAbout,
        Action onExit,
        IReadOnlyList<(string Label, Action Action)>? switchMachines,
        string disableLabel,
        Action onToggleDisable)
    {
        _projectPhase = projectPhase;
        _rows = [];
        _sepAfterClickIndex = [];
        _subItems = [];

        void AddClick(string label, Action action, bool isProject = false)
            => _rows.Add(new TrayRow { Label = label, Action = action, IsProject = isProject });

        AddClick(Loc.T("Tray.Open"), onOpen);
        AddClick(Loc.T("Tray.Reconnect"), onReconnect);
        AddClick(ProjectionUi.Tray(projectPhase).Label, onProject, isProject: true);
        AddClick(Loc.T("Tray.About"), onAbout);
        _sepAfterClickIndex.Add(CountClickable() - 1);

        _switchClickIndex = CountClickable();
        _rows.Add(new TrayRow { Label = Loc.T("Tray.Switch"), IsSubmenu = true });
        if (switchMachines != null)
            _subItems.AddRange(switchMachines);
        _subItems.Add((disableLabel, onToggleDisable));

        _sepAfterClickIndex.Add(CountClickable() - 1);
        AddClick(Loc.T("Tray.Exit"), onExit);

        _itemRects = new Rectangle[CountClickable()];
        MeasureAndBuildHwnd();
    }

    private int CountClickable()
    {
        var n = 0;
        foreach (var r in _rows)
            if (r.Clickable) n++;
        return n;
    }

    public static void ShowNearCursor(
        Action onOpen,
        Action onReconnect,
        Action onProject,
        ProjectionUiPhase projectPhase,
        Action onAbout,
        Action onExit,
        IReadOnlyList<(string Label, Action Action)>? switchMachines = null,
        string? disableLabel = null,
        Action? onToggleDisable = null)
    {
        try { _open?.Dispose(); } catch { /* ignore */ }
        var popup = new MauiTrayPopup(
            onOpen, onReconnect, onProject, projectPhase, onAbout, onExit,
            switchMachines,
            disableLabel ?? Loc.T("Hot.Off"),
            onToggleDisable ?? (() => { }));
        _open = popup;
        popup.ShowAtCursor();
    }

    private ProjectionUiChrome ProjectChrome(bool hovered)
        => ProjectionUi.Tray(_projectPhase, hovered);

    private void MeasureAndBuildHwnd()
    {
        using var font = CreateFont();
        using var iconFont = CreateMdl2Font();
        var textW = CardMinWidth - PanelPadX * 2 - ItemMargin * 2 - ItemHPad * 2;
        const int projectIconGap = 22; // MDL2 图标占位，与标题栏一致
        using (var tmp = new Bitmap(8, 8))
        using (var g = Graphics.FromImage(tmp))
        {
            foreach (var row in _rows)
            {
                var sz = g.MeasureString(row.Label, font);
                textW = Math.Max(textW, (int)Math.Ceiling(sz.Width));
            }

            // 投影：图标 + 文案（含悬停 / 各阶段最长）
            foreach (var chrome in new[]
                     {
                         ProjectChrome(false),
                         ProjectChrome(true),
                         ProjectionUi.Tray(ProjectionUiPhase.Hosting),
                         ProjectionUi.Tray(ProjectionUiPhase.Receiving)
                     })
            {
                var iconW = g.MeasureString(chrome.Mdl2Glyph, iconFont).Width;
                var labelW = g.MeasureString(chrome.Label, font).Width;
                textW = Math.Max(textW, (int)Math.Ceiling(iconW + 6 + labelW));
                textW = Math.Max(textW, projectIconGap + (int)Math.Ceiling(labelW));
            }
        }

        _cardW = Math.Max(CardMinWidth, textW + PanelPadX * 2 + ItemMargin * 2 + ItemHPad * 2);
        var sepBlock = (1 + 6 * 2) * _sepAfterClickIndex.Count;
        var clickable = CountClickable();
        _cardH = PanelPadY * 2
                 + clickable * (ItemHeight + ItemMargin * 2)
                 + sepBlock;

        // 四周等距 CardMargin
        _width = _cardW + CardMargin * 2;
        _height = _cardH + CardMargin * 2;

        var y = CardMargin + PanelPadY;
        var clickIdx = 0;
        foreach (var row in _rows)
        {
            if (!row.Clickable) continue;
            _itemRects[clickIdx] = new Rectangle(
                CardMargin + PanelPadX + ItemMargin,
                y + ItemMargin,
                _cardW - PanelPadX * 2 - ItemMargin * 2,
                ItemHeight);
            y += ItemHeight + ItemMargin * 2;
            if (_sepAfterClickIndex.Contains(clickIdx))
                y += 6 + 1 + 6;
            clickIdx++;
        }

        EnsureWndClass();
        _proc = WndProcImpl;
        var hInstance = GetModuleHandle(null);
        // 创建时就放在光标处。先建在 (0,0) 再挪过去，首次显示会从屏幕左上角闪到鼠标。
        GetCursorPos(out var pt);
        var scale = GetDpiScaleAt(pt.X, pt.Y);
        ComputePlacement(scale, out var left, out var top, out var widthPx, out var heightPx);
        _hwnd = CreateWindowEx(
            WsExLayered | WsExToolWindow | WsExTopmost,
            WndClassName,
            "",
            WsPopup,
            left, top, widthPx, heightPx,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx tray menu failed");

        // 绑定 this → GWLP_USERDATA
        var handle = GCHandle.Alloc(this);
        SetWindowLongPtr(_hwnd, GwlpUserData, GCHandle.ToIntPtr(handle));
    }

    private void ShowAtCursor()
    {
        var scale = GetDpiScale();
        ComputePlacement(scale, out var left, out var top, out var widthPx, out var heightPx);
        // 仍隐藏时就位并画好，再显示，避免空白窗从左上角移过来。
        SetWindowPos(_hwnd, HwndTopmost, left, top, widthPx, heightPx, SwpNoActivate);
        Render(scale);
        ShowWindow(_hwnd, SwShowNoActivate);
        SetForegroundWindow(_hwnd);
    }

    private void ComputePlacement(double scale, out int left, out int top, out int widthPx, out int heightPx)
    {
        GetCursorPos(out var pt);
        widthPx = Math.Max(1, (int)Math.Ceiling(_width * scale));
        heightPx = Math.Max(1, (int)Math.Ceiling(_height * scale));
        left = pt.X - widthPx + (int)(28 * scale);
        top = pt.Y - heightPx + (int)(8 * scale);
        var wa = GetWorkAreaNear(pt.X, pt.Y);
        left = Math.Max(wa.Left + 4, Math.Min(left, wa.Right - widthPx - 4));
        top = Math.Max(wa.Top + 4, Math.Min(top, wa.Bottom - heightPx - 4));
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
        catch
        {
            /* 96dpi */
        }

        return 1;
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

            // 阴影（等距边距内）
            using (var shadowPath = RoundedRect(
                       CardMargin + 1, CardMargin + 3, _cardW, _cardH, CornerRadius))
            using (var shadowBrush = new SolidBrush(Shadow))
                g.FillPath(shadowBrush, shadowPath);

            // 卡片
            using (var cardPath = RoundedRect(CardMargin, CardMargin, _cardW, _cardH, CornerRadius))
            using (var bg = new SolidBrush(CardBg))
            using (var pen = new Pen(CardBorder))
            {
                g.FillPath(bg, cardPath);
                g.DrawPath(pen, cardPath);
            }

            using var font = CreateFont();
            using var iconFont = CreateMdl2Font();
            using var textBrush = new SolidBrush(TextColor);
            using var mutedBrush = new SolidBrush(Color.FromArgb(255, 0x6B, 0x7C, 0x8F));
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            var sfRight = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };

            // 分隔线
            foreach (var after in _sepAfterClickIndex)
            {
                if (after < 0 || after >= _itemRects.Length) continue;
                var sepY = _itemRects[after].Bottom + ItemMargin + 6;
                using var sepPen = new Pen(SepColor);
                g.DrawLine(sepPen,
                    CardMargin + 12, sepY,
                    CardMargin + _cardW - 12, sepY);
            }

            var clickIdx = 0;
            foreach (var row in _rows)
            {
                if (!row.Clickable) continue;
                var rect = _itemRects[clickIdx];
                if (clickIdx == _hoverIndex)
                {
                    using var hoverPath = RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, ItemCorner);
                    using var hb = new SolidBrush(HoverBg);
                    using var hp = new Pen(HoverBorder);
                    g.FillPath(hb, hoverPath);
                    g.DrawPath(hp, hoverPath);

                    using var accentBrush = new SolidBrush(Accent);
                    g.FillRectangle(accentBrush, rect.X - 2, rect.Y + (ItemHeight - 20) / 2, 3, 20);
                }

                var hovered = clickIdx == _hoverIndex;
                if (row.IsProject)
                {
                    var chrome = ProjectChrome(hovered);
                    var fg = chrome.Emphasized ? Accent : TextColor;
                    using var fgBrush = new SolidBrush(fg);
                    var iconRect = new RectangleF(rect.X + ItemHPad, rect.Y, 18, rect.Height);
                    var labelRect = new RectangleF(rect.X + ItemHPad + 22, rect.Y,
                        Math.Max(8, rect.Width - ItemHPad * 2 - 22), rect.Height);
                    g.DrawString(chrome.Mdl2Glyph, iconFont, fgBrush, iconRect, sf);
                    g.DrawString(chrome.Label, font, fgBrush, labelRect, sf);
                }
                else if (row.IsSubmenu)
                {
                    var textRect = new RectangleF(rect.X + ItemHPad, rect.Y,
                        Math.Max(8, rect.Width - ItemHPad * 2 - 18), rect.Height);
                    var chevronRect = new RectangleF(rect.X + ItemHPad, rect.Y,
                        Math.Max(8, rect.Width - ItemHPad * 2), rect.Height);
                    g.DrawString(row.Label, font, textBrush, textRect, sf);
                    g.DrawString("\uE76C", iconFont, mutedBrush, chevronRect, sfRight);
                }
                else
                {
                    var textRect = new RectangleF(rect.X + ItemHPad, rect.Y, rect.Width - ItemHPad * 2, rect.Height);
                    g.DrawString(row.Label, font, textBrush, textRect, sf);
                }

                clickIdx++;
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
                var lx = (int)(x / scale);
                var ly = (int)(y / scale);
                var idx = HitTest(lx, ly);
                if (idx != _hoverIndex)
                {
                    _hoverIndex = idx;
                    Render(scale);
                }

                if (idx == _switchClickIndex)
                    OpenSubmenu(scale);
                else if (idx >= 0)
                    CloseSubmenu();

                TrackMouseLeave(hWnd);
                return IntPtr.Zero;
            }
            case WmMouseLeave:
                // 移入二级菜单时主菜单会收到 Leave；保留二级，勿立刻关掉
                if (_subPopup is { IsVisible: true })
                    return IntPtr.Zero;
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
                if (idx == _switchClickIndex)
                {
                    OpenSubmenu(scale);
                    return IntPtr.Zero;
                }

                var act = ActionAtClickIndex(idx);
                if (act != null)
                {
                    CloseMenu();
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try { act(); } catch { /* ignore */ }
                    });
                }

                return IntPtr.Zero;
            }
            case WmActivate:
                // 点二级菜单时主菜单会先失活；若立刻 Close 会毁掉二级，导致切换机器点不中
                if ((wParam.ToInt64() & 0xFFFF) == WaInactive)
                    ScheduleDismissIfFocusLeft();
                return IntPtr.Zero;
            case WmDestroy:
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

    private Action? ActionAtClickIndex(int clickIndex)
    {
        if (clickIndex < 0) return null;
        var i = 0;
        foreach (var row in _rows)
        {
            if (!row.Clickable) continue;
            if (i == clickIndex) return row.IsSubmenu ? null : row.Action;
            i++;
        }

        return null;
    }

    private void OpenSubmenu(double scale)
    {
        if (_switchClickIndex < 0 || _switchClickIndex >= _itemRects.Length) return;
        if (_subPopup is { IsVisible: true }) return;
        GetWindowRect(_hwnd, out var wnd);
        var item = _itemRects[_switchClickIndex];
        var anchorX = wnd.Left + (int)Math.Round((item.Right + 2) * scale);
        var anchorY = wnd.Top + (int)Math.Round((item.Top - PanelPadY) * scale);
        _subPopup?.Dispose();
        _subPopup = MauiTraySubPopup.Show(
            anchorX,
            anchorY,
            _subItems,
            onItemInvoked: CloseMenu,
            onDismissRequested: ScheduleDismissIfFocusLeft);
    }

    private void CloseSubmenu()
    {
        try { _subPopup?.Dispose(); } catch { /* ignore */ }
        _subPopup = null;
    }

    private void ScheduleDismissIfFocusLeft()
    {
        var gen = Interlocked.Increment(ref _dismissGen);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(150).ConfigureAwait(false); }
            catch { return; }

            if (gen != Volatile.Read(ref _dismissGen) || _closing) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (gen != Volatile.Read(ref _dismissGen) || _closing) return;
                if (IsPointerOverSelfOrSub()) return;
                CloseMenu();
            });
        });
    }

    private bool IsPointerOverSelfOrSub()
    {
        if (!GetCursorPos(out var pt)) return false;
        if (_hwnd != IntPtr.Zero)
        {
            GetWindowRect(_hwnd, out var main);
            if (PtInRect(ref main, pt)) return true;
        }

        var sub = _subPopup?.Hwnd ?? IntPtr.Zero;
        if (sub != IntPtr.Zero)
        {
            GetWindowRect(sub, out var subRc);
            if (PtInRect(ref subRc, pt)) return true;
        }

        return false;
    }

    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        Interlocked.Increment(ref _dismissGen);
        CloseSubmenu();
        if (ReferenceEquals(_open, this))
            _open = null;
        Dispose();
    }

    public void Dispose()
    {
        CloseSubmenu();
        if (_hwnd != IntPtr.Zero)
        {
            var h = _hwnd;
            _hwnd = IntPtr.Zero;
            try { DestroyWindow(h); } catch { /* ignore */ }
        }
    }

    private static Font CreateFont()
        => new("Microsoft YaHei UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static Font CreateMdl2Font()
        => new("Segoe MDL2 Assets", 13f, FontStyle.Regular, GraphicsUnit.Pixel);

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

    private double GetDpiScale()
    {
        try
        {
            var dpi = GetDpiForWindow(_hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
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

    private static bool _classRegistered;
    // 必须静态持有，防止被 GC 回收导致窗口回调崩溃
    private static readonly WndProc StaticWndProc = static (h, m, w, l) =>
    {
        var ud = GetWindowLongPtr(h, GwlpUserData);
        if (ud != IntPtr.Zero)
        {
            var gh = GCHandle.FromIntPtr(ud);
            if (gh.Target is MauiTrayPopup popup && popup._proc != null)
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
            hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512) // IDC_ARROW
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
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const int SwShowNoActivate = 4;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseLeave = 0x02A3;
    private const uint WmActivate = 0x0006;
    private const uint WmDestroy = 0x0002;
    private const uint WmNcDestroy = 0x0082;
    private const int WaInactive = 0;
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
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool PtInRect(ref RECT lprc, POINT pt);
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
