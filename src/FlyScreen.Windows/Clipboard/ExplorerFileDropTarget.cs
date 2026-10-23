using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.Native;

namespace FlyScreen.Win32Clipboard;

/// <summary>
/// 从资源管理器拖入文件（含管理员进程）。
/// OLE AllowDrop 在 UAC 提权后会被 UIPI 拦截；必须 RevokeDragDrop 后改用 DragAcceptFiles + WM_DROPFILES。
/// </summary>
public sealed class ExplorerFileDropTarget : IDisposable
{
    public const uint WmDropFiles = 0x0233;
    private const uint WmCopyData = 0x004A;
    private const uint WmCopyGlobalData = 0x0049;
    private const uint MsgfltAllow = 1;
    private const uint MsgfltAdd = 1;
    private static readonly IntPtr SubclassId = new(0x46534452); // 'FSDR'

    private static int _filterArmed;

    private readonly IntPtr _hwnd;
    private readonly Action<IReadOnlyList<string>> _onDropped;
    private readonly SubclassProc? _subclassProc;
    private readonly bool _useSubclass;
    private bool _disposed;

    private ExplorerFileDropTarget(
        IntPtr hwnd,
        Action<IReadOnlyList<string>> onDropped,
        bool installSubclass)
    {
        _hwnd = hwnd;
        _onDropped = onDropped;
        PrepareWindow(hwnd);

        if (installSubclass)
        {
            _subclassProc = SubclassHook;
            if (!SetWindowSubclass(hwnd, _subclassProc, SubclassId, IntPtr.Zero))
                throw new InvalidOperationException("SetWindowSubclass failed.");
            _useSubclass = true;
        }
        else
        {
            // WPF：由 HwndSource.AddHook 调用 TryHandle；此处仅准备窗口
            _useSubclass = false;
        }
    }

    /// <summary>
    /// WinUI / 通用：子类化窗口过程。
    /// </summary>
    public static ExplorerFileDropTarget? TryAttach(IntPtr hwnd, Action<IReadOnlyList<string>> onDropped)
    {
        if (hwnd == IntPtr.Zero || onDropped is null) return null;
        try
        {
            EnsureMessageFilter();
            AllowMessagesForWindow(hwnd);
            return new ExplorerFileDropTarget(hwnd, onDropped, installSubclass: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// WPF：不要子类化（与 HwndSource 冲突），只做过滤器 + RevokeDragDrop + DragAcceptFiles。
    /// 调用方用 <see cref="TryHandle"/> 从 HwndSourceHook 转发 WM_DROPFILES。
    /// </summary>
    public static ExplorerFileDropTarget? TryAttachWpf(IntPtr hwnd, Action<IReadOnlyList<string>> onDropped)
    {
        if (hwnd == IntPtr.Zero || onDropped is null) return null;
        try
        {
            EnsureMessageFilter();
            AllowMessagesForWindow(hwnd);
            return new ExplorerFileDropTarget(hwnd, onDropped, installSubclass: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>供 WPF HwndSourceHook 调用；若已处理返回 true。</summary>
    public bool TryHandle(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed || msg != unchecked((int)WmDropFiles) || wParam == IntPtr.Zero)
            return false;

        try
        {
            var paths = ReadDropFiles(wParam);
            if (paths.Count > 0)
                _onDropped(paths);
        }
        catch
        {
            // ignore
        }
        finally
        {
            DragFinish(wParam);
        }

        handled = true;
        return true;
    }

    private static void PrepareWindow(IntPtr hwnd)
    {
        // WPF 若曾 RegisterDragDrop，会吞掉 WM_DROPFILES；先撤销 OLE 投放
        try { RevokeDragDrop(hwnd); } catch { /* 未注册时失败可忽略 */ }
        DragAcceptFiles(hwnd, true);
    }

    /// <summary>布局后再调用：防止 WPF 延迟 RegisterDragDrop 盖掉 DragAcceptFiles。</summary>
    public void Reaffirm()
    {
        if (_disposed) return;
        AllowMessagesForWindow(_hwnd);
        PrepareWindow(_hwnd);
    }

    public static void EnsureMessageFilter()
    {
        if (Interlocked.Exchange(ref _filterArmed, 1) == 1) return;
        try
        {
            ChangeWindowMessageFilter(WmDropFiles, MsgfltAdd);
            ChangeWindowMessageFilter(WmCopyData, MsgfltAdd);
            ChangeWindowMessageFilter(WmCopyGlobalData, MsgfltAdd);
        }
        catch
        {
            Interlocked.Exchange(ref _filterArmed, 0);
        }
    }

    private static void AllowMessagesForWindow(IntPtr hwnd)
    {
        try
        {
            ChangeWindowMessageFilterEx(hwnd, WmDropFiles, MsgfltAllow, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WmCopyData, MsgfltAllow, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WmCopyGlobalData, MsgfltAllow, IntPtr.Zero);
        }
        catch
        {
            // ignore
        }
    }

    private IntPtr SubclassHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WmDropFiles && wParam != IntPtr.Zero)
        {
            try
            {
                var paths = ReadDropFiles(wParam);
                if (paths.Count > 0)
                    _onDropped(paths);
            }
            catch
            {
                // ignore
            }
            finally
            {
                DragFinish(wParam);
            }

            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private static List<string> ReadDropFiles(IntPtr hDrop)
    {
        var count = NativeMethods.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
        var list = new List<string>((int)count);
        var sb = new StringBuilder(520);
        for (uint i = 0; i < count; i++)
        {
            sb.Clear();
            var len = NativeMethods.DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
            if (len == 0) continue;
            var path = sb.ToString();
            if (!string.IsNullOrWhiteSpace(path))
                list.Add(path);
        }

        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            DragAcceptFiles(_hwnd, false);
            if (_useSubclass && _subclassProc != null)
                RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        }
        catch
        {
            // ignore
        }

        GC.KeepAlive(_subclassProc);
    }

    private delegate IntPtr SubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
