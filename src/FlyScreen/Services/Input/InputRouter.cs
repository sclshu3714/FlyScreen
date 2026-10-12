using System.Runtime.InteropServices;
using System.Windows;
using FlyScreen.Models;
using FlyScreen.Native;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Input;

/// <summary>
/// 低级键鼠钩子：在屏幕边缘切换控制权，远程控制时吞掉本地输入并转发。
/// </summary>
public sealed class InputRouter : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private NativeMethods.HookProc? _mouseProc;
    private NativeMethods.HookProc? _keyProc;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private bool _controllingRemote;
    private string? _activePeer;
    private int _lastX;
    private int _lastY;
    private double _virtualNx = 0.5;
    private double _virtualNy = 0.5;
    /// <summary>对端相对本机：-1 左, +1 右, -2 上, +2 下。</summary>
    private int _peerSide;

    public bool EasyMouseEnabled { get; set; } = true;
    public bool IsControllingRemote => _controllingRemote;
    public string? ActivePeer => _activePeer;

    public event Action<string?>? ControlTargetChanged;

    public InputRouter(AppSettings settings, ConnectionManager connections)
    {
        _settings = settings;
        _connections = connections;
    }

    public void Start()
    {
        _mouseProc = MouseHookCallback;
        _keyProc = KeyHookCallback;
        using var cur = System.Diagnostics.Process.GetCurrentProcess();
        using var mod = cur.MainModule!;
        var hMod = NativeMethods.GetModuleHandle(mod.ModuleName);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
        _keyHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyProc, hMod, 0);
    }

    public void Stop()
    {
        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyHook);
        _mouseHook = IntPtr.Zero;
        _keyHook = IntPtr.Zero;
    }

    public void TakeLocalControl(double? localX = null, double? localY = null)
    {
        var screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        var side = _peerSide;
        _controllingRemote = false;
        _activePeer = null;
        _peerSide = 0;
        if (localX is double x && localY is double y)
            NativeMethods.SetCursorPos((int)x, (int)y);
        else
        {
            var px = screenW / 2;
            var py = (int)(_virtualNy * Math.Max(1, screenH - 1));
            switch (side)
            {
                case -1: // 从左侧电脑回来 → 停在本机左缘内侧
                    px = 2;
                    break;
                case 1: // 从右侧回来 → 右缘内侧
                    px = screenW - 3;
                    break;
                case -2: // 从上方回来 → 上缘内侧
                    px = (int)(_virtualNx * Math.Max(1, screenW - 1));
                    py = 2;
                    break;
                case 2: // 从下方回来
                    px = (int)(_virtualNx * Math.Max(1, screenW - 1));
                    py = screenH - 3;
                    break;
            }

            NativeMethods.SetCursorPos(px, py);
        }

        ControlTargetChanged?.Invoke(null);
    }

    public void SwitchToPeer(string peer, double entryX, double entryY, int peerSide)
    {
        if (!_connections.IsPeerReachable(peer)) return;
        _controllingRemote = true;
        _activePeer = peer;
        _virtualNx = entryX;
        _virtualNy = entryY;
        _peerSide = peerSide;
        ControlTargetChanged?.Invoke(peer);
        _ = SendSwitchAsync(peer, entryX, entryY);
    }

    private async Task SendSwitchAsync(string peer, double x, double y)
    {
        try
        {
            var payload = MessageCodec.JsonBytes(new SwitchFocusPayload
            {
                TargetMachine = peer,
                EntryX = x,
                EntryY = y
            });
            await _connections.SendToAsync(peer, MessageType.SwitchFocus, payload).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        if (InputInjector.IsInjected(info.dwExtraInfo))
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

        if (!_controllingRemote && EasyMouseEnabled && _settings.EasyMouse && msg == NativeMethods.WM_MOUSEMOVE)
        {
            if (TryDetectEdgePeer(info.pt.X, info.pt.Y, screenW, screenH,
                    out var peer, out var entryX, out var entryY, out var peerSide))
            {
                SwitchToPeer(peer!, entryX, entryY, peerSide);
                if (_settings.HideMouseAtEdge)
                    NativeMethods.SetCursorPos(screenW / 2, screenH / 2);
                return (IntPtr)1;
            }
        }

        if (_controllingRemote && !string.IsNullOrEmpty(_activePeer))
        {
            if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                var dx = info.pt.X - _lastX;
                var dy = info.pt.Y - _lastY;
                // 光标被钉在中部时，用相对位移累积虚拟坐标
                if (_settings.HideMouseAtEdge)
                {
                    dx = info.pt.X - screenW / 2;
                    dy = info.pt.Y - screenH / 2;
                }

                _lastX = info.pt.X;
                _lastY = info.pt.Y;

                if (_settings.MoveMouseRelatively)
                {
                    _ = SendMouseMoveRelAsync(_activePeer, dx, dy);
                    _virtualNx = Math.Clamp(_virtualNx + dx / (double)Math.Max(1, screenW), -0.05, 1.05);
                    _virtualNy = Math.Clamp(_virtualNy + dy / (double)Math.Max(1, screenH), -0.05, 1.05);
                }
                else
                {
                    _virtualNx = Math.Clamp(_virtualNx + dx / (double)Math.Max(1, screenW), -0.05, 1.05);
                    _virtualNy = Math.Clamp(_virtualNy + dy / (double)Math.Max(1, screenH), -0.05, 1.05);
                    _ = SendMouseMoveAsync(_activePeer,
                        Math.Clamp(_virtualNx, 0, 1), Math.Clamp(_virtualNy, 0, 1));
                }

                if (ShouldReturnByVirtual())
                {
                    TakeLocalControl();
                    return (IntPtr)1;
                }

                if (_settings.HideMouseAtEdge)
                    NativeMethods.SetCursorPos(screenW / 2, screenH / 2);

                return (IntPtr)1;
            }

            if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP
                or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP)
            {
                var (button, down) = msg switch
                {
                    NativeMethods.WM_LBUTTONDOWN => (0, true),
                    NativeMethods.WM_LBUTTONUP => (0, false),
                    NativeMethods.WM_RBUTTONDOWN => (1, true),
                    NativeMethods.WM_RBUTTONUP => (1, false),
                    NativeMethods.WM_MBUTTONDOWN => (2, true),
                    _ => (2, false)
                };
                _ = SendMouseButtonAsync(_activePeer, button, down);
                return (IntPtr)1;
            }

            if (msg is NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL)
            {
                var delta = (short)((info.mouseData >> 16) & 0xFFFF);
                _ = SendWheelAsync(_activePeer, delta, msg == NativeMethods.WM_MOUSEHWHEEL);
                return (IntPtr)1;
            }
        }

        _lastX = info.pt.X;
        _lastY = info.pt.Y;
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if (InputInjector.IsInjected(info.dwExtraInfo))
            return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;

        // Ctrl+Alt+热键
        if (down && IsCtrlAlt())
        {
            if (HandleHotkey((int)info.vkCode))
                return (IntPtr)1;
        }

        if (_controllingRemote && !string.IsNullOrEmpty(_activePeer))
        {
            var extended = (info.flags & 1) != 0;
            _ = SendKeyAsync(_activePeer, (ushort)info.vkCode, down, extended);
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private bool HandleHotkey(int vk)
    {
        // 切换机器 F1-F4 / 1-4
        if (_settings.SwitchHotkeyMode != 2)
        {
            var slots = GetLayoutSlots();
            int idx = -1;
            if (_settings.SwitchHotkeyMode == 0 && vk is >= 0x70 and <= 0x73) // F1-F4 → 布局格 0-3
                idx = vk - 0x70;
            if (_settings.SwitchHotkeyMode == 1 && vk is >= 0x31 and <= 0x34) // 1-4
                idx = vk - 0x31;
            if (idx >= 0 && idx < slots.Length && !string.IsNullOrWhiteSpace(slots[idx]))
            {
                var target = slots[idx]!;
                if (string.Equals(target, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    TakeLocalControl();
                else
                    SwitchToPeer(target, 0.5, 0.5, ResolvePeerSide(target));
                return true;
            }
        }

        if (MatchKey(vk, _settings.HotkeyReconnect))
        {
            _ = _connections.ReconnectNowAsync();
            return true;
        }

        if (MatchKey(vk, _settings.HotkeyExit))
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                System.Windows.Application.Current.Shutdown());
            return true;
        }

        if (MatchKey(vk, _settings.HotkeySettings))
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                foreach (Window w in System.Windows.Application.Current.Windows)
                {
                    if (w is MainWindow mw)
                    {
                        mw.Show();
                        mw.WindowState = WindowState.Normal;
                        mw.Activate();
                    }
                }
            });
            return true;
        }

        return false;
    }

    private static bool MatchKey(int vk, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Equals("Disable", StringComparison.OrdinalIgnoreCase))
            return false;
        key = key.Trim().ToUpperInvariant();
        if (key.Length == 1) return vk == key[0];
        return false;
    }

    private static bool IsCtrlAlt()
    {
        const int VK_CONTROL = 0x11;
        const int VK_MENU = 0x12;
        return (NativeMethods.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
               && (NativeMethods.GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
    }

    /// <summary>
    /// 固定 4 格布局（与设置页矩阵一致）：
    /// 单行 [0][1][2][3]；双行 [0][1] / [2][3]。空槽占位，不压缩序号。
    /// </summary>
    private string?[] GetLayoutSlots()
    {
        _settings.EnsureLocalMachineSlot();
        var slots = new string?[4];
        for (var i = 0; i < 4; i++)
        {
            var name = i < _settings.MachineSlots.Count ? _settings.MachineSlots[i] : null;
            slots[i] = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        return slots;
    }

    private int ResolvePeerSide(string peer)
    {
        var slots = GetLayoutSlots();
        var localIdx = FindSlotIndex(slots, Environment.MachineName);
        var peerIdx = FindSlotIndex(slots, peer);
        if (localIdx < 0 || peerIdx < 0) return 0;

        var cols = _settings.TwoRow ? 2 : 4;
        var localRow = localIdx / cols;
        var localCol = localIdx % cols;
        var peerRow = peerIdx / cols;
        var peerCol = peerIdx % cols;

        if (peerRow == localRow && peerCol < localCol) return -1;
        if (peerRow == localRow && peerCol > localCol) return 1;
        if (peerCol == localCol && peerRow < localRow) return -2;
        if (peerCol == localCol && peerRow > localRow) return 2;
        // 不相邻时按主轴近似
        if (Math.Abs(peerCol - localCol) >= Math.Abs(peerRow - localRow))
            return peerCol < localCol ? -1 : 1;
        return peerRow < localRow ? -2 : 2;
    }

    private static int FindSlotIndex(string?[] slots, string name)
    {
        for (var i = 0; i < slots.Length; i++)
        {
            if (string.Equals(slots[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private bool TryDetectEdgePeer(int x, int y, int screenW, int screenH,
        out string? peer, out double entryX, out double entryY, out int peerSide)
    {
        peer = null;
        entryX = 0.5;
        entryY = screenH <= 1 ? 0.5 : Math.Clamp(y / (double)(screenH - 1), 0, 1);
        peerSide = 0;

        if (_settings.BlockMouseAtCorners)
        {
            const int c = 8;
            if ((x <= c || x >= screenW - c) && (y <= c || y >= screenH - c))
                return false;
        }

        var slots = GetLayoutSlots();
        var local = Environment.MachineName;
        var localIdx = FindSlotIndex(slots, local);
        if (localIdx < 0) return false;

        var cols = _settings.TwoRow ? 2 : 4;
        var rows = _settings.TwoRow ? 2 : 1;
        var row = localIdx / cols;
        var col = localIdx % cols;

        string? Neighbor(int r, int c)
        {
            if (_settings.WrapMouse && rows == 1)
            {
                if (c < 0) c = cols - 1;
                else if (c >= cols) c = 0;
            }

            if (r < 0 || r >= rows || c < 0 || c >= cols) return null;
            var idx = r * cols + c;
            return idx is >= 0 and < 4 ? slots[idx] : null;
        }

        bool IsValidPeer(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && !string.Equals(name, local, StringComparison.OrdinalIgnoreCase)
            && _connections.IsPeerReachable(name);

        var nx = screenW <= 1 ? 0.5 : Math.Clamp(x / (double)(screenW - 1), 0, 1);
        var ey = entryY;

        // 左边缘 → 左侧格子；进入对端右缘
        if (x <= 0)
        {
            var name = Neighbor(row, col - 1);
            if (IsValidPeer(name))
            {
                peer = name;
                peerSide = -1;
                entryX = 0.999;
                entryY = ey;
                return true;
            }
        }

        // 右边缘 → 右侧格子；进入对端左缘
        if (x >= screenW - 1)
        {
            var name = Neighbor(row, col + 1);
            if (IsValidPeer(name))
            {
                peer = name;
                peerSide = 1;
                entryX = 0.001;
                entryY = ey;
                return true;
            }
        }

        // 双行：上/下边缘
        if (_settings.TwoRow)
        {
            if (y <= 0)
            {
                var name = Neighbor(row - 1, col);
                if (IsValidPeer(name))
                {
                    peer = name;
                    peerSide = -2;
                    entryX = nx;
                    entryY = 0.999;
                    return true;
                }
            }

            if (y >= screenH - 1)
            {
                var name = Neighbor(row + 1, col);
                if (IsValidPeer(name))
                {
                    peer = name;
                    peerSide = 2;
                    entryX = nx;
                    entryY = 0.001;
                    return true;
                }
            }
        }

        return false;
    }

    private bool ShouldReturnByVirtual()
    {
        // 对端在右：向左出界回本机；在左：向右出界；上/下同理
        return _peerSide switch
        {
            1 => _virtualNx < 0,
            -1 => _virtualNx > 1,
            2 => _virtualNy < 0,
            -2 => _virtualNy > 1,
            _ => false
        };
    }

    private async Task SendMouseMoveAsync(string peer, double nx, double ny)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.MouseMove,
                MessageCodec.JsonBytes(new MouseMovePayload { Nx = nx, Ny = ny })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private async Task SendMouseMoveRelAsync(string peer, int dx, int dy)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.MouseMove,
                MessageCodec.JsonBytes(new MouseMovePayload { Relative = true, Dx = dx, Dy = dy })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private async Task SendMouseButtonAsync(string peer, int button, bool down)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.MouseButton,
                MessageCodec.JsonBytes(new MouseButtonPayload { Button = button, Down = down })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private async Task SendWheelAsync(string peer, int delta, bool horizontal)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.MouseWheel,
                MessageCodec.JsonBytes(new MouseWheelPayload { Delta = delta, Horizontal = horizontal })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private async Task SendKeyAsync(string peer, ushort vk, bool down, bool extended)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.KeyEvent,
                MessageCodec.JsonBytes(new KeyEventPayload { VirtualKey = vk, Down = down, Extended = extended })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    public void Dispose() => Stop();
}
