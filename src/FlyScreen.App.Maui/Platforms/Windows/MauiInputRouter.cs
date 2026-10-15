using System.Runtime.InteropServices;
using FlyScreen.Models;
using FlyScreen.Native;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// 低级键鼠钩子：在屏幕边缘切换控制权，远程控制时吞掉本地输入并转发。
/// </summary>
public sealed class MauiInputRouter : IDisposable
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
    /// <summary>控远端时的虚拟光标（含进入边粘滞，防止误甩回本机）。</summary>
    private readonly EdgeMouseVirtualCursor _remoteCursor = new();
    /// <summary>对端相对本机：-1 左, +1 右, -2 上, +2 下。</summary>
    private int _peerSide;
    /// <summary>切换控制权后的短冷却，避免落在边缘时立刻被环形逻辑甩回。</summary>
    private long _switchCooldownUntilTick;
    private const int SwitchCooldownMs = 900;
    /// <summary>本进程 UI/钩子线程上是否已将 ShowCursor 压到不可见（仅在该线程读写）。</summary>
    private bool _cursorHiddenOnHookThread;
    private SynchronizationContext? _hookSync;
    private long _lastCursorOwnerBroadcastTick;

    public bool EasyMouseEnabled { get; set; } = true;
    public bool IsControllingRemote => _controllingRemote;
    /// <summary>光标是否在本机（用于剪贴板文件异步预取，勿在钩子里做重活）。</summary>
    public bool IsFocusOnLocalMachine => _focusOnLocal;
    public string? ActivePeer => _activePeer;

    public event Action<string?>? ControlTargetChanged;
    public event Action? RequestShowUi;

    public MauiInputRouter(AppSettings settings, ConnectionManager connections)
    {
        _settings = settings;
        _connections = connections;
    }

    public void Start()
    {
        // LL 钩子回调跑在安装线程；ShowCursor 计数也必须只在该线程调整
        _hookSync = SynchronizationContext.Current;
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
            var (vx, vy) = _remoteCursor.Clamped01();
            var px = screenW / 2;
            var py = (int)(Math.Clamp(vy, 0.05, 0.95) * Math.Max(1, screenH - 1));
            switch (side)
            {
                case -1: // 从左侧进入本机
                    px = insetX;
                    break;
                case 1: // 从右侧进入本机
                    px = screenW - insetX;
                    break;
                case -2: // 从上方进入
                    px = (int)(Math.Clamp(vx, 0.05, 0.95) * Math.Max(1, screenW - 1));
                    py = insetY;
                    break;
                case 2: // 从下方进入
                    px = (int)(Math.Clamp(vx, 0.05, 0.95) * Math.Max(1, screenW - 1));
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
        MauiInputInjector.MoveAbsoluteNormalized(entryX, entryY);
        ControlTargetChanged?.Invoke(null);
    }

    /// <summary>对端宣告光标归属：非本机则隐藏；正在控远端且 Owner=当前对端时保持控制（勿误掐）。</summary>
    public void OnCursorOwnerChanged(string ownerMachine)
    {
        if (!_settings.HideMouseAtEdge) return;
        if (string.IsNullOrWhiteSpace(ownerMachine)) return;

        if (string.Equals(ownerMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            ShowLocalCursor();
            return;
        }

        // 本机正在控该对端：Owner=对端是预期状态（SwitchToPeer 广播的），不得放弃控制
        if (_controllingRemote
            && !string.IsNullOrEmpty(_activePeer)
            && string.Equals(ownerMachine, _activePeer, StringComparison.OrdinalIgnoreCase))
        {
            HideLocalCursor();
            _focusOnLocal = false;
            return;
        }

        // 光标归其它机器：隐藏本机光标；若在控远端则放下（第三方抢了归属）
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
        // 控对端需可达，且交集含 InjectInput（旧端无 Caps 视为具备全量本机能力）
        if (!_connections.IsPeerReachable(peer)) return;
        if (!_connections.PeerSupports(peer, PeerCaps.InjectInput)) return;
        _controllingRemote = true;
        _receivingRemoteControl = false;
        _focusOnLocal = false;
        _activePeer = peer;
        _peerSide = peerSide;
        _remoteCursor.BeginRemote(entryX, entryY, peerSide);
        ArmSwitchCooldown();
        // 无论是否隐藏光标，控远端时都必须把本机光标钉在中部，否则卡在屏幕边缘无法产生向远端方向的位移
        PinLocalCursorForRemoteControl();
        if (_settings.HideMouseAtEdge)
        {
            HideLocalCursor();
            _ = BroadcastCursorOwnerAsync(peer); // 操作面在对端，对端显示光标
        }

        ControlTargetChanged?.Invoke(peer);
        var (sx, sy) = _remoteCursor.Clamped01();
        _ = SendSwitchAsync(peer, sx, sy);
    }

    /// <summary>控远端时将本机光标钉在屏幕中部，用相对位移驱动对端虚拟坐标。</summary>
    private void PinLocalCursorForRemoteControl()
    {
        var screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        var cx = screenW / 2;
        var cy = screenH / 2;
        NativeMethods.SetCursorPos(cx, cy);
        _lastX = cx;
        _lastY = cy;
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
        if (MauiInputInjector.IsInjectedMouse(info.flags, info.dwExtraInfo))
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

        // 物理键鼠夺回：被控时不要因「移动」抢归属（注入偶发漏标会掐断控制端）；仅按键/滚轮视为本机介入
        if (_settings.HideMouseAtEdge
            && msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL)
        {
            TryClaimPhysicalInput();
        }
        else if (_settings.HideMouseAtEdge && msg == NativeMethods.WM_MOUSEMOVE
                 && !_receivingRemoteControl && !_controllingRemote)
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
                return (IntPtr)1;
            }
        }

        if (_controllingRemote && !string.IsNullOrEmpty(_activePeer))
        {
            if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                // 本机光标钉在中部：位移相对屏幕中心（与是否「隐藏边缘光标」无关）
                var dx = info.pt.X - screenW / 2;
                var dy = info.pt.Y - screenH / 2;
                _lastX = info.pt.X;
                _lastY = info.pt.Y;

                if (!_remoteCursor.TryApplyPixelDelta(dx, dy, screenW, screenH))
                {
                    // 钉中心失败导致巨位移：忽略本帧，重新钉住，避免虚拟坐标冲出界甩回本机
                    PinLocalCursorForRemoteControl();
                    return (IntPtr)1;
                }

                if (_settings.MoveMouseRelatively)
                {
                    _ = SendMouseMoveRelAsync(_activePeer, dx, dy);
                }
                else
                {
                    var (nx, ny) = _remoteCursor.Clamped01();
                    _ = SendMouseMoveAsync(_activePeer, nx, ny);
                }

                // 远端出界：粘滞 + 过冲后，按布局找下一台（支持环形）
                if (!InSwitchCooldown() && TryNavigateFromRemoteVirtual(dx, dy))
                    return (IntPtr)1;

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
        if (MauiInputInjector.IsInjectedKey(info.flags, info.dwExtraInfo))
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
        if (!_receivingRemoteControl && !_cursorHiddenOnHookThread) return; // 已是本机操作且光标可见
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

    /// <summary>
    /// ShowCursor 计数按线程维护；必须在安装 LL 钩子的 UI 线程上调整，
    /// 否则网络线程 Show 无法抵消钩子线程 Hide，玄鸟窗口上会出现「可点不可见」。
    /// </summary>
    private void RunOnHookThread(Action action)
    {
        var sc = _hookSync ?? SynchronizationContext.Current;
        if (sc == null || ReferenceEquals(SynchronizationContext.Current, sc))
        {
            action();
            return;
        }

        try
        {
            sc.Send(_ => action(), null);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void HideLocalCursor()
    {
        if (!_settings.HideMouseAtEdge) return;
        RunOnHookThread(() =>
        {
            // 确保本线程计数 < 0
            while (NativeMethods.ShowCursor(false) >= 0)
            {
            }

            _cursorHiddenOnHookThread = true;
        });
    }

    private void ShowLocalCursor()
    {
        RunOnHookThread(() =>
        {
            // 恢复本线程计数到可见 (>= 0)
            while (NativeMethods.ShowCursor(true) < 0)
            {
            }

            _cursorHiddenOnHookThread = false;
        });
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
            MainThread.BeginInvokeOnMainThread(() => Application.Current?.Quit());
            return true;
        }

        if (MatchKey(vk, _settings.HotkeySettings))
        {
            RequestShowUi?.Invoke();
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
            && _connections.IsPeerEdgeMouseCapable(name!);

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

        // 粘滞进入边 + 过冲：避免入口微抖立刻甩回本机
        if (!_remoteCursor.TryGetExitDirection(dx, dy, _settings.TwoRow, out var dirH, out var dirVSide))
            return false;

        // dirVSide 为 -2/+2；布局邻居查找用 -1/+1
        var dirV = dirVSide == 0 ? 0 : (dirVSide < 0 ? -1 : 1);

        var slots = GetLayoutSlots();
        var activeIdx = FindSlotIndex(slots, _activePeer);
        if (activeIdx < 0) return false;

        var horizontal = dirH != 0;
        var dir = horizontal ? dirH : dirV;
        var next = FindOccupiedNeighborOf(slots, activeIdx, horizontal, dir);

        if (string.IsNullOrWhiteSpace(next))
        {
            _remoteCursor.ClampInside();
            return false;
        }

        var local = Environment.MachineName;
        var (vx, vy) = _remoteCursor.Clamped01();
        if (string.Equals(next, local, StringComparison.OrdinalIgnoreCase))
        {
            // 远端往右出 → 进本机左边；远端往左出 → 进本机右边
            _peerSide = dirH != 0 ? -dirH : -dirVSide;
            if (dirH != 0)
                _remoteCursor.Set(dirH > 0 ? 0.08 : 0.92, Math.Clamp(vy, 0.05, 0.95));
            else
                _remoteCursor.Set(Math.Clamp(vx, 0.05, 0.95), dirV > 0 ? 0.08 : 0.92);
            TakeLocalControl();
            return true;
        }

        if (!_connections.IsPeerEdgeMouseCapable(next))
        {
            _remoteCursor.ClampInside();
            return false;
        }

        // 跳到另一台远端
        var entryX = dirH < 0 ? 0.92 : dirH > 0 ? 0.08 : Math.Clamp(vx, 0.05, 0.95);
        var entryY = dirV < 0 ? 0.92 : dirV > 0 ? 0.08 : Math.Clamp(vy, 0.05, 0.95);
        var peerSide = dirH != 0 ? dirH : dirVSide;
        SwitchToPeer(next, entryX, entryY, peerSide);
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

