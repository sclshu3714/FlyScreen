using System.Runtime.InteropServices;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;

namespace FlyScreen.Mac.Input;

/// <summary>
/// FlyScreen.App.Mac 在 Windows 上运行时的穿屏钩子。
/// 与 Win/Maui 的低级钩子同语义：边缘切到布局邻居，控远端时吞掉本机输入并转发。
/// </summary>
public sealed class WindowsEdgeInputRouter : IDisposable
{
    private static readonly IntPtr InjectMarker = new(0xF15C);

    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private Win32.HookProc? _mouseProc;
    private Win32.HookProc? _keyProc;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private bool _controllingRemote;
    private bool _receivingRemoteControl;
    private string? _activePeer;
    private int _lastX;
    private int _lastY;
    private int _savedLocalX;
    private int _savedLocalY;
    private bool _hasSavedLocalPos;
    private long _switchedToRemoteAtTick;
    private long _lastPhysicalInputTick;
    /// <summary>按键/点击/滚轮；拒绝对端 SwitchFocus 只用这个。</summary>
    private long _lastLocalEngageTick;
    private readonly EdgeCrossGate _edgeGate = new();
    private readonly EdgeMouseVirtualCursor _remoteCursor = new();
    private int _peerSide;
    private long _switchCooldownUntilTick;
    private const int SwitchCooldownMs = 900;
    private const int LocalActivityGuardMs = 1000;
    private const int BriefRemoteMs = 450;
    private bool _isCursorOwner = true;
    private SynchronizationContext? _hookSync;
    private long _lastCursorOwnerBroadcastTick;
    private long _edgeFileWaitStart;
    private bool _edgeDragSwallowLeftUp;

    public EdgeFileDragService? EdgeFileDrag { get; set; }
    public bool EasyMouseEnabled { get; set; } = true;
    public Func<string, bool>? IsEdgeCrossExcluded { get; set; }
    public Func<bool>? ShouldPauseEasyMouseForExtend { get; set; }
    public bool IsReceivingRemoteControl => _receivingRemoteControl;
    public bool IsControllingRemote => _controllingRemote;
    public bool HookInstalled => _mouseHook != IntPtr.Zero;

    public WindowsEdgeInputRouter(AppSettings settings, ConnectionManager connections)
    {
        _settings = settings;
        _connections = connections;
    }

    public bool Start()
    {
        if (!OperatingSystem.IsWindows()) return false;
        Stop();
        _hookSync = SynchronizationContext.Current;
        _mouseProc = MouseHookCallback;
        _keyProc = KeyHookCallback;
        using var cur = System.Diagnostics.Process.GetCurrentProcess();
        using var mod = cur.MainModule;
        var hMod = Win32.GetModuleHandle(mod?.ModuleName);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WhMouseLl, _mouseProc, hMod, 0);
        _keyHook = Win32.SetWindowsHookEx(Win32.WhKeyboardLl, _keyProc, hMod, 0);
        ShowLocalCursor();
        return _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero;
    }

    public void Stop()
    {
        ShowLocalCursor();
        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyHook);
        _mouseHook = IntPtr.Zero;
        _keyHook = IntPtr.Zero;
    }

    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        if (_receivingRemoteControl) return;
        if (!_controllingRemote
            && Environment.TickCount64 - _lastLocalEngageTick < LocalActivityGuardMs)
            return;

        _controllingRemote = false;
        _activePeer = null;
        _peerSide = 0;
        _receivingRemoteControl = true;
        ArmSwitchCooldown();
        BecomeCursorOwner(broadcast: false);
        MoveAbsoluteNormalized(entryX, entryY);
    }

    public void OnCursorOwnerChanged(string ownerMachine)
    {
        if (string.IsNullOrWhiteSpace(ownerMachine)) return;
        if (IsLocalMachine(ownerMachine))
        {
            _isCursorOwner = true;
            ShowLocalCursor();
            return;
        }

        if (_controllingRemote
            && !string.IsNullOrEmpty(_activePeer)
            && string.Equals(ownerMachine, _activePeer, StringComparison.OrdinalIgnoreCase))
        {
            _isCursorOwner = false;
            ApplyInactiveCursorPresentation();
            return;
        }

        _isCursorOwner = false;
        ApplyInactiveCursorPresentation();
        if (_controllingRemote)
        {
            _controllingRemote = false;
            _activePeer = null;
            _peerSide = 0;
        }

        _receivingRemoteControl = false;
    }

    public void MoveAbsoluteNormalized(double nx, double ny)
    {
        var vw = Win32.GetSystemMetrics(Win32.SmCxVirtualScreen);
        var vh = Win32.GetSystemMetrics(Win32.SmCyVirtualScreen);
        if (vw <= 0 || vh <= 0) return;
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        SendMouse(Win32.MouseEventMove | Win32.MouseEventAbsolute | Win32.MouseEventVirtualDesk,
            (int)Math.Round(nx * 65535), (int)Math.Round(ny * 65535), 0);
    }

    public void MoveRelative(int dx, int dy) => SendMouse(Win32.MouseEventMove, dx, dy, 0);

    public void MouseButton(MouseButtonPayload p)
    {
        uint flags = (p.Button, p.Down) switch
        {
            (0, true) => Win32.MouseEventLeftDown,
            (0, false) => Win32.MouseEventLeftUp,
            (1, true) => Win32.MouseEventRightDown,
            (1, false) => Win32.MouseEventRightUp,
            (2, true) => Win32.MouseEventMiddleDown,
            (2, false) => Win32.MouseEventMiddleUp,
            _ => 0
        };
        if (flags == 0) return;
        SendMouse(flags, 0, 0, 0);
    }

    public void MouseWheel(MouseWheelPayload p)
    {
        var flags = p.Horizontal ? Win32.MouseEventHWheel : Win32.MouseEventWheel;
        SendMouse(flags, 0, 0, unchecked((uint)p.Delta));
    }

    public void Key(KeyEventPayload p)
    {
        var input = new Win32.INPUT
        {
            type = Win32.InputKeyboard,
            U = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT
                {
                    wVk = p.VirtualKey,
                    wScan = (ushort)Win32.MapVirtualKey(p.VirtualKey, 0),
                    dwFlags = (p.Down ? 0u : Win32.KeyEventKeyUp)
                              | (p.Extended ? Win32.KeyEventExtended : 0u),
                    dwExtraInfo = InjectMarker
                }
            }
        };
        Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
    }

    public void Dispose() => Stop();

    private void SwitchToPeer(string peer, double entryX, double entryY, int peerSide)
    {
        if (!_connections.IsPeerReachable(peer)) return;
        if (!_connections.PeerSupports(peer, PeerCaps.InjectInput)) return;
        if (!_hasSavedLocalPos && _lastX != 0)
        {
            _savedLocalX = _lastX;
            _savedLocalY = _lastY;
            _hasSavedLocalPos = true;
        }

        _controllingRemote = true;
        _receivingRemoteControl = false;
        _activePeer = peer;
        _peerSide = peerSide;
        SyncEdgeCrossMode();
        _edgeGate.Reset();
        _switchedToRemoteAtTick = Environment.TickCount64;
        _remoteCursor.EntryStickyEnabled = _edgeGate.UseEntrySticky;
        _remoteCursor.BeginRemote(entryX, entryY, peerSide);
        ArmSwitchCooldown();
        _isCursorOwner = false;
        PinLocalCursorForRemoteControl();
        ApplyInactiveCursorPresentation();
        _ = BroadcastCursorOwnerAsync(peer);
        var (sx, sy) = _remoteCursor.Clamped01();
        _ = SendSwitchAsync(peer, sx, sy);
        EdgeFileDrag?.SetHover(peer);
    }

    private void TakeLocalControl()
    {
        GetVirtualBounds(out var screenL, out var screenT, out var screenW, out var screenH);
        var briefRemote = _switchedToRemoteAtTick > 0
                          && Environment.TickCount64 - _switchedToRemoteAtTick < BriefRemoteMs;
        _controllingRemote = false;
        _receivingRemoteControl = false;
        _activePeer = null;
        _peerSide = 0;
        _edgeGate.Reset();
        _switchedToRemoteAtTick = 0;
        ArmSwitchCooldown();
        if (briefRemote && _hasSavedLocalPos)
        {
            Win32.SetCursorPos(_savedLocalX, _savedLocalY);
            _lastX = _savedLocalX;
            _lastY = _savedLocalY;
        }
        else
        {
            var (vx, vy) = _remoteCursor.Clamped01();
            var x = screenL + (int)Math.Round(vx * Math.Max(1, screenW - 1));
            var y = screenT + (int)Math.Round(vy * Math.Max(1, screenH - 1));
            x = Math.Clamp(x, screenL + 8, screenL + screenW - 9);
            y = Math.Clamp(y, screenT + 8, screenT + screenH - 9);
            Win32.SetCursorPos(x, y);
            _lastX = x;
            _lastY = y;
        }

        BecomeCursorOwner(broadcast: true);
        EdgeFileDrag?.SetHover(null);
    }

    /// <summary>托盘/UI 跳转到布局中的机器（本机中央落点；对端与穿边切换同一路径）。</summary>
    public bool TrySwitchToMachine(string machineName)
    {
        if (string.IsNullOrWhiteSpace(machineName)) return false;
        machineName = machineName.Trim();
        if (IsLocalMachine(machineName))
        {
            GetVirtualBounds(out var l, out var t, out var w, out var h);
            TakeLocalControl();
            var cx = l + w / 2;
            var cy = t + h / 2;
            Win32.SetCursorPos(cx, cy);
            _lastX = cx;
            _lastY = cy;
            return true;
        }

        if (!_connections.IsPeerReachable(machineName)) return false;
        if (!_connections.PeerSupports(machineName, PeerCaps.InjectInput)) return false;
        if (IsEdgeCrossExcluded?.Invoke(machineName) == true) return false;
        SwitchToPeer(machineName, 0.5, 0.5, 0);
        return true;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
        if (IsInjectedMouse(info.flags, info.dwExtraInfo))
            return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        if (msg == Win32.WmLButtonUp)
        {
            WindowsShellDragPaths.NoteButtonUp();
            if (_edgeDragSwallowLeftUp)
            {
                _edgeDragSwallowLeftUp = false;
                if (_controllingRemote)
                    return (IntPtr)1;
            }

            if (EdgeFileDrag?.IsHolding == true)
            {
                EdgeFileDrag.Finish();
                if (_controllingRemote)
                    return (IntPtr)1;
            }
        }

        GetVirtualBounds(out var screenL, out var screenT, out var screenW, out var screenH);
        var centerX = screenL + screenW / 2;
        var centerY = screenT + screenH / 2;

        if (msg is Win32.WmMouseMove or Win32.WmLButtonDown or Win32.WmRButtonDown
            or Win32.WmMButtonDown or Win32.WmMouseWheel or Win32.WmMouseHWheel)
        {
            _lastPhysicalInputTick = Environment.TickCount64;
            if (!_controllingRemote)
            {
                _savedLocalX = info.pt.X;
                _savedLocalY = info.pt.Y;
                _hasSavedLocalPos = true;
            }
        }

        if (msg is Win32.WmLButtonDown or Win32.WmRButtonDown or Win32.WmMButtonDown
            or Win32.WmMouseWheel or Win32.WmMouseHWheel)
        {
            _lastLocalEngageTick = Environment.TickCount64;
            TryClaimPhysicalInput();
        }

        if (msg == Win32.WmLButtonDown && !_controllingRemote && _settings.TransferFile)
            WindowsShellDragPaths.NoteButtonDown();
        else if (msg == Win32.WmMouseMove && !_controllingRemote)
            TryClaimPhysicalInput();

        if (!_controllingRemote && msg == Win32.WmMouseMove)
        {
            SyncEdgeCrossMode();
            var nxObs = screenW <= 1 ? 0.5 : Math.Clamp((info.pt.X - screenL) / (double)(screenW - 1), 0, 1);
            var nyObs = screenH <= 1 ? 0.5 : Math.Clamp((info.pt.Y - screenT) / (double)(screenH - 1), 0, 1);
            var rightObs = screenL + screenW - 1;
            var bottomObs = screenT + screenH - 1;
            var onEdge = info.pt.X <= screenL || info.pt.X >= rightObs
                         || info.pt.Y <= screenT || info.pt.Y >= bottomObs;
            _edgeGate.Observe(nxObs, nyObs, onEdge, Environment.TickCount64);

            var leftDown = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
            var nearEdge = NearVirtualEdge(info.pt.X, info.pt.Y, screenL, screenT, screenW, screenH);
            if (_settings.TransferFile && leftDown && nearEdge)
                WindowsShellDragPaths.PrefetchIfNeeded();

            var dragged = Array.Empty<string>();
            var haveDrag = _settings.TransferFile && leftDown && onEdge
                           && WindowsShellDragPaths.TryGet(out dragged);
            if (_settings.TransferFile && leftDown && onEdge && !haveDrag)
            {
                if (_edgeFileWaitStart == 0)
                    _edgeFileWaitStart = Environment.TickCount64;
                if (Environment.TickCount64 - _edgeFileWaitStart < 400)
                    return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }
            else if (!onEdge || !leftDown)
            {
                _edgeFileWaitStart = 0;
            }

            var pauseExtend = ShouldPauseEasyMouseForExtend?.Invoke() == true;
            if (EasyMouseEnabled && _settings.EasyMouse && !pauseExtend && !InSwitchCooldown()
                && TryDetectEdgePeer(info.pt.X, info.pt.Y, screenL, screenT, screenW, screenH,
                    out var peer, out var entryX, out var entryY, out var peerSide))
            {
                if (haveDrag)
                    EdgeFileDrag?.TryBegin(dragged);
                SwitchToPeer(peer!, entryX, entryY, peerSide);
                if (haveDrag)
                    DismissLocalDrag();
                return (IntPtr)1;
            }
        }

        if (_controllingRemote && !string.IsNullOrEmpty(_activePeer))
        {
            if (msg == Win32.WmMouseMove)
            {
                var dx = info.pt.X - centerX;
                var dy = info.pt.Y - centerY;
                _lastX = info.pt.X;
                _lastY = info.pt.Y;
                if (!_remoteCursor.TryApplyPixelDelta(dx, dy, screenW, screenH))
                {
                    PinLocalCursorForRemoteControl();
                    return (IntPtr)1;
                }

                if (_settings.MoveMouseRelatively)
                    _ = SendMouseMoveRelAsync(_activePeer, dx, dy);
                else
                {
                    var (nx, ny) = _remoteCursor.Clamped01();
                    _ = SendMouseMoveAsync(_activePeer, nx, ny);
                }

                SyncEdgeCrossMode();
                if (!InSwitchCooldown() && TryNavigateFromRemoteVirtual(dx, dy))
                    return (IntPtr)1;

                Win32.SetCursorPos(centerX, centerY);
                return (IntPtr)1;
            }

            if (msg is Win32.WmLButtonDown or Win32.WmLButtonUp
                or Win32.WmRButtonDown or Win32.WmRButtonUp
                or Win32.WmMButtonDown or Win32.WmMButtonUp)
            {
                if (EdgeFileDrag?.IsHolding == true)
                    return (IntPtr)1;
                var (button, down) = msg switch
                {
                    Win32.WmLButtonDown => (0, true),
                    Win32.WmLButtonUp => (0, false),
                    Win32.WmRButtonDown => (1, true),
                    Win32.WmRButtonUp => (1, false),
                    Win32.WmMButtonDown => (2, true),
                    _ => (2, false)
                };
                _ = SendMouseButtonAsync(_activePeer, button, down);
                return (IntPtr)1;
            }

            if (msg is Win32.WmMouseWheel or Win32.WmMouseHWheel)
            {
                if (EdgeFileDrag?.IsHolding == true)
                    return (IntPtr)1;
                var delta = (short)((info.mouseData >> 16) & 0xFFFF);
                _ = SendWheelAsync(_activePeer, delta, msg == Win32.WmMouseHWheel);
                return (IntPtr)1;
            }
        }

        _lastX = info.pt.X;
        _lastY = info.pt.Y;
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Win32.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
        if (IsInjectedKey(info.flags, info.dwExtraInfo))
            return Win32.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var down = wParam.ToInt32() is Win32.WmKeyDown or Win32.WmSysKeyDown;
        if (down)
        {
            _lastPhysicalInputTick = Environment.TickCount64;
            _lastLocalEngageTick = Environment.TickCount64;
            TryClaimPhysicalInput();
        }

        if (EdgeFileDrag?.IsHolding == true && _controllingRemote)
        {
            if (down && info.vkCode == 0x1B)
            {
                EdgeFileDrag.Cancel();
                _edgeDragSwallowLeftUp = true;
            }

            return (IntPtr)1;
        }

        if (_controllingRemote && !string.IsNullOrEmpty(_activePeer))
        {
            var extended = (info.flags & 1) != 0;
            _ = SendKeyAsync(_activePeer, (ushort)info.vkCode, down, extended);
            return (IntPtr)1;
        }

        return Win32.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private void TryClaimPhysicalInput()
    {
        if (_controllingRemote) return;
        if (_isCursorOwner && !_receivingRemoteControl) return;
        if (InSwitchCooldown() && _receivingRemoteControl) return;
        _receivingRemoteControl = false;
        BecomeCursorOwner(broadcast: true);
    }

    private void BecomeCursorOwner(bool broadcast)
    {
        _isCursorOwner = true;
        ShowLocalCursor();
        if (broadcast)
            _ = BroadcastCursorOwnerAsync(LocalPeerIdentity.MachineName);
    }

    private void ApplyInactiveCursorPresentation()
    {
        if (_settings.HideMouseAtEdge)
            HideLocalCursor();
        else
        {
            ShowLocalCursor();
            PinLocalCursorForRemoteControl();
        }
    }

    private void PinLocalCursorForRemoteControl()
    {
        GetVirtualBounds(out var left, out var top, out var width, out var height);
        var cx = left + width / 2;
        var cy = top + height / 2;
        Win32.SetCursorPos(cx, cy);
        _lastX = cx;
        _lastY = cy;
    }

    private void ArmSwitchCooldown()
        => _switchCooldownUntilTick = Environment.TickCount64 + SwitchCooldownMs;

    private bool InSwitchCooldown() => Environment.TickCount64 < _switchCooldownUntilTick;

    private bool TryDetectEdgePeer(int x, int y, int screenL, int screenT, int screenW, int screenH,
        out string? peer, out double entryX, out double entryY, out int peerSide)
    {
        peer = null;
        entryX = 0.5;
        entryY = screenH <= 1 ? 0.5 : Math.Clamp((y - screenT) / (double)(screenH - 1), 0, 1);
        peerSide = 0;

        var right = screenL + screenW - 1;
        var bottom = screenT + screenH - 1;
        if (_settings.BlockMouseAtCorners)
        {
            const int c = 8;
            if ((x <= screenL + c || x >= right - c) && (y <= screenT + c || y >= bottom - c))
                return false;
        }

        var slots = GetLayoutSlots();
        var localIdx = FindLocalSlotIndex(slots);
        if (localIdx < 0) return false;

        bool IsValidPeer(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && !IsLocalMachine(name)
            && _connections.IsPeerEdgeMouseCapable(name!)
            && IsEdgeCrossExcluded?.Invoke(name!) != true;

        var nx = screenW <= 1 ? 0.5 : Math.Clamp((x - screenL) / (double)(screenW - 1), 0, 1);
        var now = Environment.TickCount64;

        if (x <= screenL && x <= _lastX)
        {
            var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: true, dir: -1);
            if (IsValidPeer(name) && _edgeGate.TryAdmit(-1, now))
            {
                peer = name;
                peerSide = -1;
                entryX = 0.999;
                return true;
            }

            return false;
        }

        if (x >= right && x >= _lastX)
        {
            var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: true, dir: +1);
            if (IsValidPeer(name) && _edgeGate.TryAdmit(1, now))
            {
                peer = name;
                peerSide = 1;
                entryX = 0.001;
                return true;
            }

            return false;
        }

        if (_settings.TwoRow)
        {
            if (y <= screenT && y <= _lastY)
            {
                var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: false, dir: -1);
                if (IsValidPeer(name) && _edgeGate.TryAdmit(-2, now))
                {
                    peer = name;
                    peerSide = -2;
                    entryX = nx;
                    entryY = 0.999;
                    return true;
                }

                return false;
            }

            if (y >= bottom && y >= _lastY)
            {
                var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: false, dir: +1);
                if (IsValidPeer(name) && _edgeGate.TryAdmit(2, now))
                {
                    peer = name;
                    peerSide = 2;
                    entryX = nx;
                    entryY = 0.001;
                    return true;
                }

                return false;
            }
        }

        return false;
    }

    private bool TryNavigateFromRemoteVirtual(int dx, int dy)
    {
        if (string.IsNullOrEmpty(_activePeer)) return false;
        var now = Environment.TickCount64;
        var (cx0, cy0) = _remoteCursor.Clamped01();
        if (!_remoteCursor.TryGetExitDirection(dx, dy, _settings.TwoRow, out var dirH, out var dirVSide))
        {
            var onGeomEdge = _remoteCursor.Nx <= 0 || _remoteCursor.Nx >= 1
                             || (_settings.TwoRow && (_remoteCursor.Ny <= 0 || _remoteCursor.Ny >= 1));
            _edgeGate.Observe(cx0, cy0, onGeomEdge, now);
            return false;
        }

        var exitSide = dirH != 0 ? dirH : dirVSide;
        if (!_edgeGate.TryAdmitRemoteExit(exitSide, cx0, cy0, dx, dy, now))
        {
            if (_edgeGate.ShouldClampVirtualCursorOnReject)
                _remoteCursor.ClampInside();
            return false;
        }

        var dirV = dirVSide == 0 ? 0 : (dirVSide < 0 ? -1 : 1);
        var slots = GetLayoutSlots();
        var activeIdx = FindSlotIndex(slots, _activePeer);
        if (activeIdx < 0) return false;
        var horizontal = dirH != 0;
        var next = FindOccupiedNeighborOf(slots, activeIdx, horizontal, horizontal ? dirH : dirV);
        if (string.IsNullOrWhiteSpace(next))
        {
            _remoteCursor.ClampInside();
            return false;
        }

        var (vx, vy) = _remoteCursor.Clamped01();
        if (IsLocalMachine(next))
        {
            _peerSide = dirH != 0 ? -dirH : -dirVSide;
            if (dirH != 0)
                _remoteCursor.Set(dirH > 0 ? 0.08 : 0.92, Math.Clamp(vy, 0.05, 0.95));
            else
                _remoteCursor.Set(Math.Clamp(vx, 0.05, 0.95), dirV > 0 ? 0.08 : 0.92);
            TakeLocalControl();
            return true;
        }

        if (!_connections.IsPeerEdgeMouseCapable(next) || IsEdgeCrossExcluded?.Invoke(next) == true)
        {
            _remoteCursor.ClampInside();
            return false;
        }

        var entryX = dirH < 0 ? 0.92 : dirH > 0 ? 0.08 : Math.Clamp(vx, 0.05, 0.95);
        var entryY = dirV < 0 ? 0.92 : dirV > 0 ? 0.08 : Math.Clamp(vy, 0.05, 0.95);
        SwitchToPeer(next, entryX, entryY, dirH != 0 ? dirH : dirVSide);
        return true;
    }

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

    private string? FindOccupiedNeighborOf(string?[] slots, int fromIndex, bool horizontal, int dir)
    {
        _ = slots;
        return LayoutEdge.Neighbor(_settings, fromIndex, horizontal, dir);
    }

    private int FindLocalSlotIndex(string?[] slots)
    {
        var idx = FindSlotIndex(slots, LocalPeerIdentity.MachineName);
        if (idx >= 0) return idx;
        var env = Environment.MachineName;
        if (!LocalPeerIdentity.IsBogusMachineName(env))
            return FindSlotIndex(slots, env);
        return -1;
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

    private static bool NearVirtualEdge(int x, int y, int screenL, int screenT, int screenW, int screenH, int margin = 28)
    {
        var right = screenL + screenW - 1;
        var bottom = screenT + screenH - 1;
        return x <= screenL + margin || x >= right - margin
               || y <= screenT + margin || y >= bottom - margin;
    }

    private static void DismissLocalDrag()
    {
        var down = new Win32.INPUT
        {
            type = Win32.InputKeyboard,
            U = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT { wVk = 0x1B, dwExtraInfo = InjectMarker }
            }
        };
        var up = down;
        up.U.ki.dwFlags = Win32.KeyEventKeyUp;
        Win32.SendInput(1, [down], Marshal.SizeOf<Win32.INPUT>());
        Win32.SendInput(1, [up], Marshal.SizeOf<Win32.INPUT>());
    }

    private static bool IsLocalMachine(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        var env = Environment.MachineName;
        return !LocalPeerIdentity.IsBogusMachineName(env)
               && string.Equals(name, env, StringComparison.OrdinalIgnoreCase);
    }

    private void SyncEdgeCrossMode()
    {
        _edgeGate.Mode = _settings.EdgeCrossMode switch
        {
            1 => EdgeCrossMode.DoubleTap,
            2 => EdgeCrossMode.SpeedBurst,
            3 => EdgeCrossMode.HotkeyOnly,
            _ => EdgeCrossMode.BarrierFree
        };
        if (_controllingRemote)
            _remoteCursor.EntryStickyEnabled = _edgeGate.UseEntrySticky;
    }

    private void HideLocalCursor()
    {
        if (!_settings.HideMouseAtEdge) return;
        RunOnHookThread(() =>
        {
            while (Win32.ShowCursor(false) >= 0) { }
        });
    }

    private void ShowLocalCursor()
    {
        RunOnHookThread(() =>
        {
            while (Win32.ShowCursor(true) < 0) { }
        });
    }

    private void RunOnHookThread(Action action)
    {
        var sc = _hookSync ?? SynchronizationContext.Current;
        if (sc == null || ReferenceEquals(SynchronizationContext.Current, sc))
        {
            action();
            return;
        }

        try { sc.Post(_ => { try { action(); } catch { /* ignore */ } }, null); }
        catch { /* ignore */ }
    }

    private async Task BroadcastCursorOwnerAsync(string ownerMachine)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastCursorOwnerBroadcastTick < 80) return;
            _lastCursorOwnerBroadcastTick = now;
            await _connections.BroadcastAsync(MessageType.CursorOwner,
                MessageCodec.JsonBytes(new CursorOwnerPayload { OwnerMachine = ownerMachine })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private async Task SendSwitchAsync(string peer, double x, double y)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.SwitchFocus,
                MessageCodec.JsonBytes(new SwitchFocusPayload { TargetMachine = peer, EntryX = x, EntryY = y }))
                .ConfigureAwait(false);
        }
        catch { /* ignore */ }
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
                MessageCodec.JsonBytes(new KeyEventPayload
                {
                    VirtualKey = vk,
                    Down = down,
                    Extended = extended
                })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private static void SendMouse(uint flags, int dx, int dy, uint data)
    {
        var input = new Win32.INPUT
        {
            type = Win32.InputMouse,
            U = new Win32.InputUnion
            {
                mi = new Win32.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = data,
                    dwFlags = flags,
                    dwExtraInfo = InjectMarker
                }
            }
        };
        Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
    }

    private static bool IsInjectedMouse(uint flags, IntPtr extra)
        => extra == InjectMarker || (flags & (Win32.LlmhfInjected | Win32.LlmhfLowerIlInjected)) != 0;

    private static bool IsInjectedKey(uint flags, IntPtr extra)
        => extra == InjectMarker || (flags & Win32.LlkfInjected) != 0;

    private static void GetVirtualBounds(out int left, out int top, out int width, out int height)
    {
        left = Win32.GetSystemMetrics(Win32.SmXVirtualScreen);
        top = Win32.GetSystemMetrics(Win32.SmYVirtualScreen);
        width = Win32.GetSystemMetrics(Win32.SmCxVirtualScreen);
        height = Win32.GetSystemMetrics(Win32.SmCyVirtualScreen);
        if (width > 0 && height > 0) return;
        left = 0;
        top = 0;
        width = Math.Max(1, Win32.GetSystemMetrics(Win32.SmCxScreen));
        height = Math.Max(1, Win32.GetSystemMetrics(Win32.SmCyScreen));
    }

    private static class Win32
    {
        public const int WhMouseLl = 14;
        public const int WhKeyboardLl = 13;
        public const int WmKeyDown = 0x0100;
        public const int WmSysKeyDown = 0x0104;
        public const int WmMouseMove = 0x0200;
        public const int WmLButtonDown = 0x0201;
        public const int WmLButtonUp = 0x0202;
        public const int WmRButtonDown = 0x0204;
        public const int WmRButtonUp = 0x0205;
        public const int WmMButtonDown = 0x0207;
        public const int WmMButtonUp = 0x0208;
        public const int WmMouseWheel = 0x020A;
        public const int WmMouseHWheel = 0x020E;
        public const int SmCxScreen = 0;
        public const int SmCyScreen = 1;
        public const int SmXVirtualScreen = 76;
        public const int SmYVirtualScreen = 77;
        public const int SmCxVirtualScreen = 78;
        public const int SmCyVirtualScreen = 79;
        public const uint InputMouse = 0;
        public const uint InputKeyboard = 1;
        public const uint MouseEventMove = 0x0001;
        public const uint MouseEventLeftDown = 0x0002;
        public const uint MouseEventLeftUp = 0x0004;
        public const uint MouseEventRightDown = 0x0008;
        public const uint MouseEventRightUp = 0x0010;
        public const uint MouseEventMiddleDown = 0x0020;
        public const uint MouseEventMiddleUp = 0x0040;
        public const uint MouseEventWheel = 0x0800;
        public const uint MouseEventHWheel = 0x1000;
        public const uint MouseEventAbsolute = 0x8000;
        public const uint MouseEventVirtualDesk = 0x4000;
        public const uint KeyEventKeyUp = 0x0002;
        public const uint KeyEventExtended = 0x0001;
        public const uint LlmhfInjected = 0x00000001;
        public const uint LlmhfLowerIlInjected = 0x00000002;
        public const uint LlkfInjected = 0x00000010;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern int ShowCursor(bool bShow);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
