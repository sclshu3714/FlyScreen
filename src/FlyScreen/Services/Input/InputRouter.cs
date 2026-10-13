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
    /// <summary>正在被其它电脑远程操控（光标由对端注入）。</summary>
    private bool _receivingRemoteControl;
    /// <summary>光标焦点是否在本机（本机操作或被远程操控本机）。</summary>
    private bool _focusOnLocal = true;
    private string? _activePeer;
    private int _lastX;
    private int _lastY;
    private double _virtualNx = 0.5;
    private double _virtualNy = 0.5;
    /// <summary>对端相对本机：-1 左, +1 右, -2 上, +2 下。</summary>
    private int _peerSide;
    /// <summary>切换控制权后的短冷却，避免落在边缘时立刻被环形逻辑甩回。</summary>
    private long _switchCooldownUntilTick;
    private const int SwitchCooldownMs = 350;
    private int _cursorHideDepth;
    private long _lastCursorOwnerBroadcastTick;

    public bool EasyMouseEnabled { get; set; } = true;
    public bool IsControllingRemote => _controllingRemote;
    /// <summary>光标是否在本机（用于剪贴板文件异步预取，勿在钩子里做重活）。</summary>
    public bool IsFocusOnLocalMachine => _focusOnLocal;
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
        ShowLocalCursor();
    }

    public void Stop()
    {
        ShowLocalCursor();
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
        _receivingRemoteControl = false;
        _focusOnLocal = true;
        _activePeer = null;
        _peerSide = 0;
        ArmSwitchCooldown();
        if (localX is double x && localY is double y)
            NativeMethods.SetCursorPos((int)x, (int)y);
        else
        {
            // 落点靠内侧，避免立刻再次触发边缘切换（环形时尤其明显）
            var insetX = Math.Max(24, screenW / 12);
            var insetY = Math.Max(24, screenH / 12);
            var px = screenW / 2;
            var py = (int)(Math.Clamp(_virtualNy, 0.05, 0.95) * Math.Max(1, screenH - 1));
            switch (side)
            {
                case -1: // 从左侧进入本机
                    px = insetX;
                    break;
                case 1: // 从右侧进入本机
                    px = screenW - insetX;
                    break;
                case -2: // 从上方进入
                    px = (int)(Math.Clamp(_virtualNx, 0.05, 0.95) * Math.Max(1, screenW - 1));
                    py = insetY;
                    break;
                case 2: // 从下方进入
                    px = (int)(Math.Clamp(_virtualNx, 0.05, 0.95) * Math.Max(1, screenW - 1));
                    py = screenH - insetY;
                    break;
            }

            NativeMethods.SetCursorPos(px, py);
            _lastX = px;
            _lastY = py;
        }

        // 本机重新成为操作机：显示本机光标，并通知其它机器隐藏
        BecomeCursorOwner(broadcast: true);
        ControlTargetChanged?.Invoke(null);
    }

    /// <summary>对端 SwitchFocus 到本机：显示光标并进入「被远程控制」状态。</summary>
    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        _controllingRemote = false;
        _activePeer = null;
        _peerSide = 0;
        _receivingRemoteControl = true;
        _focusOnLocal = true;
        ArmSwitchCooldown();
        BecomeCursorOwner(broadcast: false); // 发送方已广播 Owner=本机
        InputInjector.MoveAbsoluteNormalized(entryX, entryY);
        ControlTargetChanged?.Invoke(null);
    }

    /// <summary>对端宣告光标归属：非本机则隐藏并停止控制远端。</summary>
    public void OnCursorOwnerChanged(string ownerMachine)
    {
        if (!_settings.HideMouseAtEdge) return;
        if (string.IsNullOrWhiteSpace(ownerMachine)) return;

        if (string.Equals(ownerMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            ShowLocalCursor();
            return;
        }

        // 其它电脑正在操作：隐藏本机光标，并放下对远端的控制
        HideLocalCursor();
        _focusOnLocal = false;
        if (_controllingRemote)
        {
            _controllingRemote = false;
            _activePeer = null;
            _peerSide = 0;
            ControlTargetChanged?.Invoke(null);
        }

        _receivingRemoteControl = false;
    }

    public void SwitchToPeer(string peer, double entryX, double entryY, int peerSide)
    {
        if (!_connections.IsPeerReachable(peer)) return;
        _controllingRemote = true;
        _receivingRemoteControl = false;
        _focusOnLocal = false;
        _activePeer = peer;
        // 入口略向内收，避免刚进入就因抖动从同一边甩回去
        _virtualNx = Math.Clamp(entryX, 0.02, 0.98);
        _virtualNy = Math.Clamp(entryY, 0.02, 0.98);
        _peerSide = peerSide;
        ArmSwitchCooldown();
        if (_settings.HideMouseAtEdge)
        {
            HideLocalCursor();
            _ = BroadcastCursorOwnerAsync(peer); // 操作面在对端，对端显示光标
        }

        ControlTargetChanged?.Invoke(peer);
        _ = SendSwitchAsync(peer, _virtualNx, _virtualNy);
    }

    private void ArmSwitchCooldown()
        => _switchCooldownUntilTick = Environment.TickCount64 + SwitchCooldownMs;

    private bool InSwitchCooldown()
        => Environment.TickCount64 < _switchCooldownUntilTick;

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
        if (InputInjector.IsInjectedMouse(info.flags, info.dwExtraInfo))
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

        // 物理鼠标活动：夺回本机光标显示，并让其它电脑隐藏（含「正被远程控制时动了本机鼠标」）
        if (_settings.HideMouseAtEdge
            && msg is NativeMethods.WM_MOUSEMOVE or NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL)
        {
            TryClaimPhysicalInput();
        }

        if (!_controllingRemote && EasyMouseEnabled && _settings.EasyMouse && msg == NativeMethods.WM_MOUSEMOVE
            && !InSwitchCooldown())
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

                // 放宽虚拟出界范围，保证左右两侧都能稳定触发
                const double lo = -0.2;
                const double hi = 1.2;
                if (_settings.MoveMouseRelatively)
                {
                    _ = SendMouseMoveRelAsync(_activePeer, dx, dy);
                    _virtualNx = Math.Clamp(_virtualNx + dx / (double)Math.Max(1, screenW), lo, hi);
                    _virtualNy = Math.Clamp(_virtualNy + dy / (double)Math.Max(1, screenH), lo, hi);
                }
                else
                {
                    _virtualNx = Math.Clamp(_virtualNx + dx / (double)Math.Max(1, screenW), lo, hi);
                    _virtualNy = Math.Clamp(_virtualNy + dy / (double)Math.Max(1, screenH), lo, hi);
                    _ = SendMouseMoveAsync(_activePeer,
                        Math.Clamp(_virtualNx, 0, 1), Math.Clamp(_virtualNy, 0, 1));
                }

                // 远端出界：按「当前控制机器」在布局中的位置找下一台（支持环形）
                if (!InSwitchCooldown() && TryNavigateFromRemoteVirtual(dx, dy))
                    return (IntPtr)1;

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
        if (InputInjector.IsInjectedKey(info.flags, info.dwExtraInfo))
            return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;

        if (_settings.HideMouseAtEdge && down)
            TryClaimPhysicalInput();

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

    /// <summary>
    /// 本机物理键鼠活动：显示本机光标，广播「光标归本机」，停止被远程控制状态。
    /// 正在主动控制远端时不抢（光标应留在对端）。
    /// </summary>
    private void TryClaimPhysicalInput()
    {
        if (!_settings.HideMouseAtEdge) return;
        if (_controllingRemote) return; // 本机正在控远端：继续隐藏本机光标，输入仍转发
        if (!_receivingRemoteControl && _cursorHideDepth == 0) return; // 已是本机操作且光标可见
        if (InSwitchCooldown() && _receivingRemoteControl) return; // 刚切入被控，忽略切入瞬间误判

        _receivingRemoteControl = false;
        BecomeCursorOwner(broadcast: true);
    }

    private void BecomeCursorOwner(bool broadcast)
    {
        ShowLocalCursor();
        if (broadcast && _settings.HideMouseAtEdge)
            _ = BroadcastCursorOwnerAsync(Environment.MachineName);
    }

    private void HideLocalCursor()
    {
        if (!_settings.HideMouseAtEdge) return;
        // ShowCursor 为线程引用计数；确保隐藏到不可见
        while (NativeMethods.ShowCursor(false) >= 0)
            _cursorHideDepth++;
        if (_cursorHideDepth == 0) _cursorHideDepth = 1;
    }

    private void ShowLocalCursor()
    {
        // 恢复到可见（计数 >= 0）
        while (NativeMethods.ShowCursor(true) < 0)
        {
        }

        _cursorHideDepth = 0;
    }

    private async Task BroadcastCursorOwnerAsync(string ownerMachine)
    {
        try
        {
            // 防抖：避免物理移动每帧狂广播
            var now = Environment.TickCount64;
            if (now - _lastCursorOwnerBroadcastTick < 80) return;
            _lastCursorOwnerBroadcastTick = now;

            var payload = MessageCodec.JsonBytes(new CursorOwnerPayload { OwnerMachine = ownerMachine });
            await _connections.BroadcastAsync(MessageType.CursorOwner, payload).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
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

        bool IsValidPeer(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && !string.Equals(name, local, StringComparison.OrdinalIgnoreCase)
            && _connections.IsPeerReachable(name);

        var nx = screenW <= 1 ? 0.5 : Math.Clamp(x / (double)(screenW - 1), 0, 1);
        var ey = entryY;

        // 左边缘 → 左侧机器；进入对端右缘（环形时最左→最右）
        if (x <= 0)
        {
            var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: true, dir: -1);
            if (IsValidPeer(name))
            {
                peer = name;
                peerSide = -1;
                entryX = 0.999;
                entryY = ey;
                return true;
            }
        }

        // 右边缘 → 右侧机器；进入对端左缘（环形时最右→最左）
        if (x >= screenW - 1)
        {
            var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: true, dir: +1);
            if (IsValidPeer(name))
            {
                peer = name;
                peerSide = 1;
                entryX = 0.001;
                entryY = ey;
                return true;
            }
        }

        // 双行：上/下边缘（环形时同列首尾相连）
        if (_settings.TwoRow)
        {
            if (y <= 0)
            {
                var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: false, dir: -1);
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
                var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: false, dir: +1);
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

    /// <summary>
    /// 控制远端时虚拟光标出界：按远端机器在布局中的位置找下一台。
    /// 下一台是本机则收回控制；是其它电脑则切换过去（环形两端可互通）。
    /// </summary>
    private bool TryNavigateFromRemoteVirtual(int dx, int dy)
    {
        if (string.IsNullOrEmpty(_activePeer)) return false;

        // 必须「已经顶到边 + 仍朝该方向推」，左右对称，避免只一边能出
        var dirH = 0;
        var dirV = 0;
        if (_virtualNx <= 0 && dx < 0) dirH = -1;
        else if (_virtualNx >= 1 && dx > 0) dirH = 1;
        else if (_settings.TwoRow && _virtualNy <= 0 && dy < 0) dirV = -1;
        else if (_settings.TwoRow && _virtualNy >= 1 && dy > 0) dirV = 1;
        else return false;

        var slots = GetLayoutSlots();
        var activeIdx = FindSlotIndex(slots, _activePeer);
        if (activeIdx < 0) return false;

        var horizontal = dirH != 0;
        var dir = horizontal ? dirH : dirV;
        var next = FindOccupiedNeighborOf(slots, activeIdx, horizontal, dir);

        if (string.IsNullOrWhiteSpace(next))
        {
            // 到头（未开环形）：夹回界内
            _virtualNx = Math.Clamp(_virtualNx, 0, 1);
            _virtualNy = Math.Clamp(_virtualNy, 0, 1);
            return false;
        }

        var local = Environment.MachineName;
        if (string.Equals(next, local, StringComparison.OrdinalIgnoreCase))
        {
            // 远端往右出 → 进本机左边；远端往左出 → 进本机右边
            _peerSide = dirH != 0 ? -dirH : -dirV;
            if (dirH != 0)
            {
                _virtualNx = dirH > 0 ? 0.08 : 0.92;
                _virtualNy = Math.Clamp(_virtualNy, 0.05, 0.95);
            }
            else
            {
                _virtualNy = dirV > 0 ? 0.08 : 0.92;
                _virtualNx = Math.Clamp(_virtualNx, 0.05, 0.95);
            }
            TakeLocalControl();
            return true;
        }

        if (!_connections.IsPeerReachable(next))
        {
            _virtualNx = Math.Clamp(_virtualNx, 0, 1);
            _virtualNy = Math.Clamp(_virtualNy, 0, 1);
            return false;
        }

        // 跳到另一台远端
        var entryX = dirH < 0 ? 0.92 : dirH > 0 ? 0.08 : Math.Clamp(_virtualNx, 0.05, 0.95);
        var entryY = dirV < 0 ? 0.92 : dirV > 0 ? 0.08 : Math.Clamp(_virtualNy, 0.05, 0.95);
        var peerSide = dirH != 0 ? dirH : dirV;
        SwitchToPeer(next, entryX, entryY, peerSide);
        if (_settings.HideMouseAtEdge)
        {
            var screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            var screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            NativeMethods.SetCursorPos(screenW / 2, screenH / 2);
        }

        return true;
    }

    /// <summary>
    /// 在已占用格子中找邻居。空位忽略。
    /// WrapMouse：首尾相连；否则到头返回 null。
    /// </summary>
    private string? FindOccupiedNeighborOf(string?[] slots, int fromIndex, bool horizontal, int dir)
    {
        if (fromIndex < 0 || dir == 0) return null;

        var cols = _settings.TwoRow ? 2 : 4;
        var rows = _settings.TwoRow ? 2 : 1;
        var row = fromIndex / cols;
        var col = fromIndex % cols;

        var occupied = new List<int>();
        if (horizontal)
        {
            for (var c = 0; c < cols; c++)
            {
                var name = slots[row * cols + c];
                if (!string.IsNullOrWhiteSpace(name))
                    occupied.Add(c);
            }
        }
        else
        {
            for (var r = 0; r < rows; r++)
            {
                var name = slots[r * cols + col];
                if (!string.IsNullOrWhiteSpace(name))
                    occupied.Add(r);
            }
        }

        if (occupied.Count == 0) return null;
        var current = horizontal ? col : row;
        var myPos = occupied.IndexOf(current);
        if (myPos < 0) return null;

        int nextPos;
        if (_settings.WrapMouse)
        {
            if (occupied.Count < 2) return null;
            nextPos = (myPos + dir + occupied.Count) % occupied.Count;
        }
        else
        {
            nextPos = myPos + dir;
            if (nextPos < 0 || nextPos >= occupied.Count) return null;
        }

        var target = occupied[nextPos];
        return horizontal
            ? slots[row * cols + target]
            : slots[target * cols + col];
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

    public void Dispose()
    {
        ShowLocalCursor();
        Stop();
    }
}
