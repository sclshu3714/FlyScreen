using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FlyScreen.Input;
using FlyScreen.Models;
using FlyScreen.ScreenExtend;
using FlyScreen.Native;
using FlyScreen.Services;
using FlyScreen.Services.Clipboard;
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
    /// <summary>切入远端前本机光标位置；短暂误切回来时恢复，避免甩到屏幕左侧 inset。</summary>
    private int _savedLocalX;
    private int _savedLocalY;
    private bool _hasSavedLocalPos;
    private long _switchedToRemoteAtTick;
    /// <summary>本机最近一次物理键鼠时间（含移动）；用于保存光标位置等。</summary>
    private long _lastPhysicalInputTick;
    /// <summary>本机最近一次按键/滚轮/点击；仅此用于拒绝对端 SwitchFocus（勿把鼠标微动当「正在操作」）。</summary>
    private long _lastLocalEngageTick;
    /// <summary>穿边门控（无障碍 / 双击 / 极速）。</summary>
    private readonly EdgeCrossGate _edgeGate = new();
    /// <summary>控远端时的虚拟光标（含进入边粘滞，防止误甩回本机）。</summary>
    private readonly EdgeMouseVirtualCursor _remoteCursor = new();
    /// <summary>对端相对本机：-1 左, +1 右, -2 上, +2 下。</summary>
    private int _peerSide;
    /// <summary>切换控制权后的短冷却，避免落在边缘时立刻被环形逻辑甩回。</summary>
    private long _switchCooldownUntilTick;
    private const int SwitchCooldownMs = 900;
    private const int LocalActivityGuardMs = 1000;
    private const int BriefRemoteMs = 450;
    /// <summary>本机是否为当前活动电脑（显示操作光标的那一台）。</summary>
    private bool _isCursorOwner = true;
    private Dispatcher? _hookDispatcher;
    private long _lastCursorOwnerBroadcastTick;
    private RemoteCompositionHost? _composition;
    private long _edgeFileWaitStart;
    private bool _edgeDragSwallowLeftUp;
    private int _edgeStickSide;
    private long _edgeStickTick;
    private const int EdgeStickAdmitMs = 80;

    /// <summary>按住文件拖过屏幕边缘时，把路径交给布局里的下一台。</summary>
    public EdgeFileDragService? EdgeFileDrag { get; set; }

    public bool EasyMouseEnabled { get; set; } = true;
    /// <summary>扩展屏会话：排除该对端名的穿边切入。</summary>
    public Func<string, bool>? IsEdgeCrossExcluded { get; set; }
    /// <summary>扩展屏从机：暂停本机 EasyMouse。</summary>
    public Func<bool>? ShouldPauseEasyMouseForExtend { get; set; }
    public bool IsControllingRemote => _controllingRemote;
    /// <summary>是否接受对端键鼠注入（须先经 SwitchFocus 进入被控）。</summary>
    public bool IsReceivingRemoteControl => _receivingRemoteControl;
    /// <summary>光标是否在本机（用于剪贴板文件异步预取，勿在钩子里做重活）。</summary>
    public bool IsFocusOnLocalMachine => _focusOnLocal;
    public string? ActivePeer => _activePeer;

    public event Action<string?>? ControlTargetChanged;

    /// <summary>本机 Ctrl+V / Shift+Insert：返回 true 表示已处理（吞键）。</summary>
    public Func<bool>? TryHandleLocalFilePasteHotkey { get; set; }

    /// <summary>本机右键：可能打开粘贴菜单，可提前拉取远程文件。</summary>
    public Action? PrefetchRemoteFilesForLikelyPaste { get; set; }

    public InputRouter(AppSettings settings, ConnectionManager connections)
    {
        _settings = settings;
        _connections = connections;
    }

    public void Start()
    {
        // LL 钩子回调跑在安装线程；该线程必须有消息泵（须在 WPF UI 线程调用）
        Stop();
        _hookDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        EnsureCompositionHost();
        _mouseProc = MouseHookCallback;
        _keyProc = KeyHookCallback;
        using var cur = System.Diagnostics.Process.GetCurrentProcess();
        using var mod = cur.MainModule!;
        var hMod = NativeMethods.GetModuleHandle(mod.ModuleName);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
        _keyHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyProc, hMod, 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            AppLog.Error($"键鼠钩子安装失败 mouse={_mouseHook} key={_keyHook} win32={err} thread={Environment.CurrentManagedThreadId}");
        }
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
        VirtualScreen.GetBounds(out var screenL, out var screenT, out var screenW, out var screenH);
        var side = _peerSide;
        var briefRemote = _switchedToRemoteAtTick > 0
                          && Environment.TickCount64 - _switchedToRemoteAtTick < BriefRemoteMs;
        _controllingRemote = false;
        _receivingRemoteControl = false;
        _focusOnLocal = true;
        _activePeer = null;
        _peerSide = 0;
        _edgeGate.Reset();
        _switchedToRemoteAtTick = 0;
        ArmSwitchCooldown();
        if (localX is double x && localY is double y)
        {
            NativeMethods.SetCursorPos((int)x, (int)y);
            _lastX = (int)x;
            _lastY = (int)y;
        }
        else if (briefRemote && _hasSavedLocalPos)
        {
            // 误触边缘立刻回来：回到切入前位置，不要甩到左侧 inset（用户报告的「突然跳到左边」）
            NativeMethods.SetCursorPos(_savedLocalX, _savedLocalY);
            _lastX = _savedLocalX;
            _lastY = _savedLocalY;
        }
        else
        {
            // 落点靠内侧，避免立刻再次触发边缘切换（环形时尤其明显）
            var insetX = Math.Max(48, screenW / 8);
            var insetY = Math.Max(48, screenH / 8);
            var (vx, vy) = _remoteCursor.Clamped01();
            var px = screenL + screenW / 2;
            var py = screenT + (int)(Math.Clamp(vy, 0.05, 0.95) * Math.Max(1, screenH - 1));
            switch (side)
            {
                case -1: // 从左侧进入本机
                    px = screenL + insetX;
                    break;
                case 1: // 从右侧进入本机
                    px = screenL + screenW - insetX;
                    break;
                case -2: // 从上方进入
                    px = screenL + (int)(Math.Clamp(vx, 0.05, 0.95) * Math.Max(1, screenW - 1));
                    py = screenT + insetY;
                    break;
                case 2: // 从下方进入
                    px = screenL + (int)(Math.Clamp(vx, 0.05, 0.95) * Math.Max(1, screenW - 1));
                    py = screenT + screenH - insetY;
                    break;
            }

            NativeMethods.SetCursorPos(px, py);
            _lastX = px;
            _lastY = py;
        }

        _composition?.Deactivate();
        // 本机重新成为操作机：显示本机光标，并通知其它机器隐藏
        BecomeCursorOwner(broadcast: true);
        ControlTargetChanged?.Invoke(null);
        EdgeFileDrag?.SetHover(null);
    }

    /// <summary>对端 SwitchFocus 到本机：显示光标并进入「被远程控制」状态。</summary>
    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        // 已在被控：忽略重复 SwitchFocus，避免 AbsPointer 每次触控都把光标拽回入口点
        if (_receivingRemoteControl) return;

        // 本机正在「主动操作」（点击/滚轮/按键）时拒绝对端拽走光标。
        // 勿用鼠标微动：触控板/光电噪声会让对端永远穿不进来。
        if (!_controllingRemote
            && Environment.TickCount64 - _lastLocalEngageTick < LocalActivityGuardMs)
        {
            AppLog.Debug("忽略 SwitchFocus：本机刚有点击/按键");
            return;
        }

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

    /// <summary>
    /// 对端宣告活动电脑：启用「隐藏光标」时非活动机隐藏；关闭时非活动机居中可见。
    /// 正在控远端且 Owner=当前对端时保持控制（勿误掐）。
    /// </summary>
    public void OnCursorOwnerChanged(string ownerMachine)
    {
        if (string.IsNullOrWhiteSpace(ownerMachine)) return;

        if (IsLocalMachine(ownerMachine))
        {
            _isCursorOwner = true;
            ShowLocalCursor();
            return;
        }

        // 本机正在控该对端：Owner=对端是预期状态（SwitchToPeer 广播的），不得放弃控制
        if (_controllingRemote
            && !string.IsNullOrEmpty(_activePeer)
            && string.Equals(ownerMachine, _activePeer, StringComparison.OrdinalIgnoreCase))
        {
            _isCursorOwner = false;
            _focusOnLocal = false;
            ApplyInactiveCursorPresentation();
            return;
        }

        // 活动权归其它机器：本机变为非活动；若在控远端则放下（第三方抢了归属）
        _isCursorOwner = false;
        _focusOnLocal = false;
        ApplyInactiveCursorPresentation();
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
        if (_hasSavedLocalPos == false && _lastX != 0)
        {
            _savedLocalX = _lastX;
            _savedLocalY = _lastY;
            _hasSavedLocalPos = true;
        }

        _controllingRemote = true;
        _receivingRemoteControl = false;
        _focusOnLocal = false;
        _activePeer = peer;
        _peerSide = peerSide;
        SyncEdgeCrossMode();
        _edgeGate.Reset();
        _switchedToRemoteAtTick = Environment.TickCount64;
        _remoteCursor.EntryStickyEnabled = _edgeGate.UseEntrySticky;
        _remoteCursor.BeginRemote(entryX, entryY, peerSide);
        ArmSwitchCooldown();
        // 控远端时本机光标钉在中部采集位移；活动面在对端
        _isCursorOwner = false;
        PinLocalCursorForRemoteControl();
        ApplyInactiveCursorPresentation();
        _ = BroadcastCursorOwnerAsync(peer);

        ControlTargetChanged?.Invoke(peer);
        var (sx, sy) = _remoteCursor.Clamped01();
        _ = SendSwitchAsync(peer, sx, sy);
        EdgeFileDrag?.SetHover(peer);

        if (PeerComposition.NeedsHostComposition(_connections.GetPeerRemoteInfo(peer)))
            _composition?.Activate(peer);
        else
            _composition?.Deactivate();
    }

    private void EnsureCompositionHost()
    {
        if (_composition != null || _hookDispatcher == null) return;
        _composition = new RemoteCompositionHost(_hookDispatcher);
        _composition.CompositionUpdated += (peer, payload) =>
            _ = SendCompositionUpdateAsync(peer, payload);
        _composition.TextCommitted += (peer, text) =>
            _ = SendTextInputAsync(peer, text);
        _composition.CompositionCancelled += peer =>
            _ = SendCompositionCancelAsync(peer);
    }

    /// <summary>手机点选候选 / 翻页。</summary>
    public void HandleCompositionSelect(string peer, CompositionSelectPayload? payload)
    {
        if (payload == null) return;
        if (!string.Equals(peer, _activePeer, StringComparison.OrdinalIgnoreCase)) return;
        if (_composition == null || !_composition.IsActive) return;
        _composition.SelectCandidate(payload.Index, payload.Text);
    }

    /// <summary>手机点空白 / 切应用：取消组字。</summary>
    public void HandleCompositionCancel(string peer)
    {
        if (!string.Equals(peer, _activePeer, StringComparison.OrdinalIgnoreCase)) return;
        if (_composition == null || !_composition.IsActive) return;
        _composition.CancelComposition();
    }

    /// <summary>控远端时将本机光标钉在虚拟桌面中部，用相对位移驱动对端虚拟坐标。</summary>
    private void PinLocalCursorForRemoteControl()
    {
        VirtualScreen.GetCenter(out var cx, out var cy);
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
        var injectedMouse = InputInjector.IsInjectedMouse(info.flags, info.dwExtraInfo);
        ProjectionHostCursor.OnHookMouse(injectedMouse);
        if (injectedMouse)
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        if (msg == NativeMethods.WM_LBUTTONUP)
        {
            WindowsDragPaths.NoteButtonUp();
            EdgeFileDragCapture.Disarm();
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
        VirtualScreen.GetBounds(out var screenL, out var screenT, out var screenW, out var screenH);
        var centerX = screenL + screenW / 2;
        var centerY = screenT + screenH / 2;

        // 记录本机物理活动（注入鼠标已在上方过滤）
        if (msg is NativeMethods.WM_MOUSEMOVE or NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL)
        {
            _lastPhysicalInputTick = Environment.TickCount64;
            if (!_controllingRemote)
            {
                _savedLocalX = info.pt.X;
                _savedLocalY = info.pt.Y;
                _hasSavedLocalPos = true;
            }
        }

        // 物理键鼠夺回活动权（注入已在上方过滤，故被控时移动也可夺回）
        if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL)
        {
            _lastLocalEngageTick = Environment.TickCount64;
            TryClaimPhysicalInput();
        }

        // 穿边拖文件：只记按下时刻；真正读 Shell 在靠近边缘时 Prefetch（避免每次点击起线程）
        if (msg == NativeMethods.WM_LBUTTONDOWN && !_controllingRemote && _settings.TransferFile)
            WindowsDragPaths.NoteButtonDown();

        // 本机右键：可能粘贴，提前拉取远程文件填入占位路径
        if (msg == NativeMethods.WM_RBUTTONDOWN && !_controllingRemote && _focusOnLocal)
        {
            try { PrefetchRemoteFilesForLikelyPaste?.Invoke(); }
            catch { /* ignore */ }
        }
        else if (msg == NativeMethods.WM_MOUSEMOVE && !_controllingRemote)
        {
            TryClaimPhysicalInput();
        }

        if (!_controllingRemote && msg == NativeMethods.WM_MOUSEMOVE)
        {
            SyncEdgeCrossMode();
            var nx = screenW <= 1 ? 0.5 : Math.Clamp((info.pt.X - screenL) / (double)(screenW - 1), 0, 1);
            var ny = screenH <= 1 ? 0.5 : Math.Clamp((info.pt.Y - screenT) / (double)(screenH - 1), 0, 1);
            var right = screenL + screenW - 1;
            var bottom = screenT + screenH - 1;
            var onEdge = info.pt.X <= screenL || info.pt.X >= right || info.pt.Y <= screenT || info.pt.Y >= bottom;
            UpdateEdgeStick(info.pt.X, info.pt.Y, screenL, screenT, right, bottom);
            _edgeGate.Observe(nx, ny, onEdge, Environment.TickCount64);

            var leftDown = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0;
            // 仅靠近屏幕外缘才挂接收条 / 预读选中项（低频路径）
            var nearEdge = NearVirtualEdge(info.pt.X, info.pt.Y, screenL, screenT, screenW, screenH);
            if (_settings.TransferFile && leftDown && nearEdge)
            {
                EdgeFileDragCapture.Arm();
                WindowsDragPaths.PrefetchIfNeeded();
            }
            else
                EdgeFileDragCapture.Disarm();
            var dragged = Array.Empty<string>();
            var haveDrag = _settings.TransferFile && leftDown && onEdge
                           && WindowsDragPaths.TryGet(out dragged);
            if (_settings.TransferFile && leftDown && onEdge && !haveDrag)
            {
                if (_edgeFileWaitStart == 0)
                    _edgeFileWaitStart = Environment.TickCount64;
                if (Environment.TickCount64 - _edgeFileWaitStart < 400)
                    return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }
            else if (!onEdge || !leftDown)
            {
                _edgeFileWaitStart = 0;
            }

            var pauseExtend = ShouldPauseEasyMouseForExtend?.Invoke() == true;
            if (EasyMouseEnabled && _settings.EasyMouse && !pauseExtend && !InSwitchCooldown())
            {
                if (TryDetectEdgePeer(info.pt.X, info.pt.Y, screenL, screenT, screenW, screenH,
                        out var peer, out var entryX, out var entryY, out var peerSide))
                {
                    if (haveDrag && EdgeFileDrag?.TryBegin(dragged) == true)
                    {
                        EdgeFileDragCapture.DismissLocalDrag();
                        EdgeFileDragCapture.Disarm();
                    }

                    SwitchToPeer(peer!, entryX, entryY, peerSide);
                    return (IntPtr)1;
                }
            }
        }

        if (_controllingRemote && !string.IsNullOrEmpty(_activePeer))
        {
            if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                // 本机光标钉在虚拟桌面中部：位移相对该中心（与是否「隐藏边缘光标」无关）
                var dx = info.pt.X - centerX;
                var dy = info.pt.Y - centerY;
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

                // 远端出界：粘滞 + 过冲后，再经穿边门控，按布局找下一台（支持环形）
                SyncEdgeCrossMode();
                if (!InSwitchCooldown() && TryNavigateFromRemoteVirtual(dx, dy))
                    return (IntPtr)1;

                NativeMethods.SetCursorPos(centerX, centerY);
                return (IntPtr)1;
            }

            if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP
                or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP)
            {
                if (EdgeFileDrag?.IsHolding == true)
                    return (IntPtr)1;
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
                if (EdgeFileDrag?.IsHolding == true)
                    return (IntPtr)1;
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

        // 先于热键/转发：记录物理修饰键（忽略注入，避免热键抬起注入破坏链式 F1→F2）
        ModifierKeyRelease.ObservePhysicalKey((int)info.vkCode, down);

        if (down)
        {
            _lastPhysicalInputTick = Environment.TickCount64;
            _lastLocalEngageTick = Environment.TickCount64;
            TryClaimPhysicalInput();
        }

        // Ctrl+Alt+热键（成功后必须松开修饰键，否则切入远端后本机 KEYUP 被吞会粘滞）
        if (down && ModifierKeyRelease.IsCtrlAltHeld())
        {
            var peerBefore = _controllingRemote ? _activePeer : null;
            var triggerVk = (ushort)info.vkCode;
            if (HandleHotkey(triggerVk))
            {
                ReleaseModifiersAfterHotkey(peerBefore, triggerVk);
                return (IntPtr)1;
            }
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

        // 本机懒粘贴：Ctrl+V / Shift+Insert 先拉取再重放
        if (down && !_controllingRemote && _focusOnLocal)
        {
            var vk = (int)info.vkCode;
            var isPaste = (vk == 0x56 && IsCtrlDown() && !IsAltDown() && !IsWinDown())
                          || (vk == 0x2D && IsShiftDown() && !IsCtrlDown() && !IsAltDown());
            if (isPaste && TryHandleLocalFilePasteHotkey?.Invoke() == true)
                return (IntPtr)1;
        }

        if (_controllingRemote && !string.IsNullOrEmpty(_activePeer))
        {
            var extended = (info.flags & 1) != 0;
            var vk = (ushort)info.vkCode;
            if (TryRouteCompositionKey(_activePeer, vk, down, extended))
                return (IntPtr)1;

            _ = SendKeyAsync(_activePeer, vk, down, extended);
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 控手机/平板：可打印键与组字相关键进本机隐藏 IME；修饰键/功能键仍直发对端。
    /// </summary>
    private bool TryRouteCompositionKey(string peer, ushort vk, bool down, bool extended)
    {
        if (_composition == null) return false;
        if (!PeerComposition.NeedsHostComposition(_connections.GetPeerRemoteInfo(peer)))
            return false;

        // Ctrl/Alt/Win 组合留给对端快捷键
        if (IsCtrlDown() || IsAltDown() || IsWinDown())
            return false;

        // 功能键、导航键（非组字时）直发；组字中方向/翻页/Esc/退格进宿主
        if (IsFunctionKey(vk))
            return false;

        var composing = _composition.IsComposing;
        var candidateUi = _composition.HasCandidateSession;
        if (vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x21 or 0x22) // arrows / page
        {
            // 有拼音缓冲或候选时即进本机导航（不单靠 IsComposing，否则上下左右无效）
            if (!candidateUi) return false;
            _composition.FeedKey(vk, down, extended);
            return true;
        }

        if (vk == 0x1B) // Esc
        {
            if (candidateUi)
            {
                if (down) _composition.CancelComposition();
                return true;
            }
            return false;
        }

        if (vk == 0x0D) // Enter：组字确认进宿主，否则对端换行
        {
            if (composing || candidateUi)
            {
                _composition.FeedKey(vk, down, extended);
                return true;
            }
            return false;
        }

        // 字母数字符号、空格、OEM —— 进宿主组字/英文直出
        // 未组字时的退格/Delete 发给手机（Monaco），避免被空宿主吞掉
        if (IsCompositionFeedKey(vk))
        {
            if (!composing && !candidateUi && vk is 0x08 or 0x2E)
                return false;
            _composition.FeedKey(vk, down, extended);
            return true;
        }

        return false;
    }

    private static bool IsCompositionFeedKey(ushort vk)
        => vk is (>= 0x30 and <= 0x39) // 0-9
            or (>= 0x41 and <= 0x5A) // A-Z
            or (>= 0x60 and <= 0x69) // numpad
            or 0x08 or 0x20 // Backspace Space
            or 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF or 0xC0 // OEM
            or 0xDB or 0xDC or 0xDD or 0xDE
            or 0x10 or 0xA0 or 0xA1 or 0x14; // Shift / Caps（组字大小写）

    private static bool IsFunctionKey(ushort vk)
        => vk is (>= 0x70 and <= 0x8B) // F1-F24
            or 0x2E or 0x24 or 0x23 or 0x2D // Del Home End Insert
            or 0x09 or 0x5B or 0x5C; // Tab Win

    private static bool IsCtrlDown() => ModifierKeyRelease.IsCtrlHeld();
    private static bool IsShiftDown() => ModifierKeyRelease.IsShiftHeld();
    private static bool IsAltDown() => ModifierKeyRelease.IsAltHeld();
    private static bool IsWinDown() => ModifierKeyRelease.IsWinHeld();

    /// <summary>
    /// 本机物理键鼠活动：成为活动电脑、显示光标，并通知其它机器进入非活动态。
    /// 正在主动控制远端时不抢（活动面应留在对端）。
    /// </summary>
    private void TryClaimPhysicalInput()
    {
        if (_controllingRemote) return; // 本机正在控远端：输入仍转发，活动面在对端
        if (_isCursorOwner && !_receivingRemoteControl) return; // 已是活动机
        if (InSwitchCooldown() && _receivingRemoteControl) return; // 刚切入被控，忽略切入瞬间误判

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

    /// <summary>
    /// 退出集群 / 布局仅剩本机 / 断开前：结束远端控制态并强制显示本机光标
    /// （避免「隐藏光标」后只剩本机仍看不见鼠标）。
    /// </summary>
    public void RestoreCursorWhenLeavingCluster()
    {
        var wasRemote = _controllingRemote || _receivingRemoteControl || !_isCursorOwner;
        _controllingRemote = false;
        _receivingRemoteControl = false;
        _activePeer = null;
        _peerSide = 0;
        _focusOnLocal = true;
        _isCursorOwner = true;
        _edgeGate.Reset();
        _composition?.Deactivate();
        ShowLocalCursor();
        if (wasRemote)
        {
            try { ControlTargetChanged?.Invoke(null); } catch { /* ignore */ }
        }
    }

    /// <summary>非活动机：启用隐藏则藏光标；关闭则居中可见。</summary>
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

    /// <summary>
    /// ShowCursor 计数按线程维护；必须在安装 LL 钩子的 UI 线程上调整，
    /// 否则网络线程 Show 无法抵消钩子线程 Hide，玄鸟窗口上会出现「可点不可见」。
    /// </summary>
    private void RunOnHookThread(Action action)
    {
        var d = _hookDispatcher ?? Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            // 同步：切入本机时需在注入光标划过 WPF 窗口前恢复可见
            d.Invoke(action, DispatcherPriority.Send);
        }
        catch (TaskCanceledException)
        {
            // 关闭中
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 已关闭
        }
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
        // 切换机器 F1-F4 / 1-4 → 布局固定 4 格（单行左→右；双行上排 F1 F2、下排 F3 F4）
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
                if (IsLocalMachine(target))
                {
                    // 热键回本机：落点屏幕中央，避免落在穿边 inset 又立刻被无障碍穿走
                    VirtualScreen.GetBounds(out var l, out var t, out var w, out var h);
                    TakeLocalControl(l + w * 0.5, t + h * 0.5);
                }
                else if (!TryHotkeySwitchToPeer(target))
                    return true; // 已识别热键，即使对端不可用也吞掉，避免把 F 键打进当前对端
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

        if (MatchKey(vk, _settings.HotkeyLock))
        {
            EasyMouseEnabled = !EasyMouseEnabled;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 热键触发后：对端补发修饰键抬起；本机注入抬起，避免切换控制权后物理 KEYUP 被吞。
    /// </summary>
    private void ReleaseModifiersAfterHotkey(string? peerThatReceivedMods, ushort triggerVk)
    {
        if (!string.IsNullOrEmpty(peerThatReceivedMods))
        {
            foreach (var (vk, ext) in ModifierKeyRelease.All)
                _ = SendKeyAsync(peerThatReceivedMods, vk, down: false, extended: ext);
            _ = SendKeyAsync(peerThatReceivedMods, triggerVk, down: false, extended: false);
        }

        foreach (var (vk, ext) in ModifierKeyRelease.All)
        {
            if (!ModifierKeyRelease.IsPhysicallyDown(vk)) continue;
            InputInjector.Key(new KeyEventPayload
            {
                VirtualKey = vk,
                Down = false,
                Extended = ext
            });
        }

        if (ModifierKeyRelease.IsPhysicallyDown(triggerVk))
        {
            InputInjector.Key(new KeyEventPayload
            {
                VirtualKey = triggerVk,
                Down = false,
                Extended = false
            });
        }
    }

    private static bool MatchKey(int vk, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Equals("Disable", StringComparison.OrdinalIgnoreCase))
            return false;
        key = key.Trim().ToUpperInvariant();
        if (key.Length == 1) return vk == key[0];
        return false;
    }

    /// <summary>
    /// 托盘/UI 强制跳转到布局中的机器：任意穿边模式可用，落点屏幕中央。
    /// 与鼠标穿边门控 / EasyMouse / SwitchHotkeyMode 无关。
    /// </summary>
    public bool TrySwitchToMachine(string machineName)
    {
        if (string.IsNullOrWhiteSpace(machineName)) return false;
        machineName = machineName.Trim();

        // 清掉穿边冷却与门控，避免刚跳转又被上一帧贴边逻辑拽走
        _switchCooldownUntilTick = 0;
        _edgeGate.Reset();

        if (IsLocalMachine(machineName))
        {
            VirtualScreen.GetBounds(out var l, out var t, out var w, out var h);
            TakeLocalControl(l + w * 0.5, t + h * 0.5);
            return true;
        }

        if (!_connections.IsPeerReachable(machineName))
        {
            AppLog.Warn($"托盘切换失败：对端不可达 {machineName}");
            return false;
        }

        if (!_connections.PeerSupports(machineName, PeerCaps.InjectInput))
        {
            AppLog.Warn($"托盘切换失败：对端无 InjectInput（手机/平板请开启无障碍）{machineName}");
            return false;
        }

        if (IsEdgeCrossExcluded?.Invoke(machineName) == true)
        {
            AppLog.Warn($"托盘切换失败：对端被扩展屏排除 {machineName}");
            return false;
        }

        // peerSide=0：中央落点，不启用进入边粘滞（与穿边入口切换区分）
        SwitchToPeer(machineName, 0.5, 0.5, peerSide: 0);
        return true;
    }

    /// <summary>热键切换到布局中的对端（含手机/平板：有 InjectInput 即可，不要求 EasyMouse）。</summary>
    private bool TryHotkeySwitchToPeer(string peer)
    {
        if (!_connections.IsPeerReachable(peer))
        {
            AppLog.Warn($"热键切换失败：对端不可达 {peer}");
            return false;
        }

        // 手机/平板靠无障碍注入；与穿边门控的 EdgeMouse 要求分开
        if (!_connections.PeerSupports(peer, PeerCaps.InjectInput))
        {
            AppLog.Warn($"热键切换失败：对端无 InjectInput（手机/平板请开启无障碍）{peer}");
            return false;
        }

        if (IsEdgeCrossExcluded?.Invoke(peer) == true)
        {
            AppLog.Warn($"热键切换失败：对端被扩展屏排除 {peer}");
            return false;
        }

        SwitchToPeer(peer, 0.5, 0.5, ResolvePeerSide(peer));
        return true;
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
            if (string.IsNullOrWhiteSpace(name))
            {
                slots[i] = null;
                continue;
            }

            // 投影中：仅当远程已具备穿边能力时才改写目标；否则保留本地名，避免残留「投影中」导致整边失效
            slots[i] = ResolveEdgePeerNameForInput(name.Trim());
        }

        return slots;
    }

    private string ResolveEdgePeerNameForInput(string localSlotName)
    {
        var resolved = RemoteBindingHelper.ResolveEdgePeerName(_settings, localSlotName);
        if (string.IsNullOrWhiteSpace(resolved)
            || string.Equals(resolved, localSlotName, StringComparison.OrdinalIgnoreCase))
            return localSlotName;
        if (_connections.IsPeerEdgeMouseCapable(resolved))
            return resolved;
        return localSlotName;
    }

    private int ResolvePeerSide(string peer)
    {
        var slots = GetLayoutSlots();
        var localIdx = FindLocalSlotIndex(slots);
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

    private int FindLocalSlotIndex(string?[] slots)
    {
        // 用物理槽位找本机：投影改写后 slots[i] 可能是远程名
        _ = slots;
        _settings.EnsureLocalMachineSlot();
        for (var i = 0; i < 4; i++)
        {
            var physical = i < _settings.MachineSlots.Count ? _settings.MachineSlots[i] : null;
            if (!string.IsNullOrWhiteSpace(physical) && IsLocalMachine(physical))
                return i;
        }

        return -1;
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

    /// <summary>距虚拟桌面外缘 ≤ margin 像素（用于提前挂拖放接收条，又不干扰中央拖影）。</summary>
    private static bool NearVirtualEdge(int x, int y, int screenL, int screenT, int screenW, int screenH, int margin = 28)
    {
        var right = screenL + screenW - 1;
        var bottom = screenT + screenH - 1;
        return x <= screenL + margin || x >= right - margin
               || y <= screenT + margin || y >= bottom - margin;
    }

    /// <summary>
    /// 仅在「虚拟桌面」外缘且继续向外移动时切换，避免副屏整块被当成主屏右/左边缘。
    /// </summary>
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
        var ey = entryY;

        var now = Environment.TickCount64;

        // 继续外推，或已贴边停留足够久（夹住光标后常无「更外」位移）
        if (x <= screenL && (x <= _lastX || EdgeStickReady(-1)))
        {
            var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: true, dir: -1);
            if (IsValidPeer(name) && _edgeGate.TryAdmit(-1, now))
            {
                peer = name;
                peerSide = -1;
                entryX = 0.999;
                entryY = ey;
                return true;
            }

            return false;
        }

        if (x >= right && (x >= _lastX || EdgeStickReady(1)))
        {
            var name = FindOccupiedNeighborOf(slots, localIdx, horizontal: true, dir: +1);
            if (IsValidPeer(name) && _edgeGate.TryAdmit(1, now))
            {
                peer = name;
                peerSide = 1;
                entryX = 0.001;
                entryY = ey;
                return true;
            }

            return false;
        }

        if (_settings.TwoRow)
        {
            if (y <= screenT && (y <= _lastY || EdgeStickReady(-2)))
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

            if (y >= bottom && (y >= _lastY || EdgeStickReady(2)))
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

    private void UpdateEdgeStick(int x, int y, int screenL, int screenT, int right, int bottom)
    {
        var side = 0;
        if (x <= screenL) side = -1;
        else if (x >= right) side = 1;
        else if (_settings.TwoRow && y <= screenT) side = -2;
        else if (_settings.TwoRow && y >= bottom) side = 2;

        if (side == 0)
        {
            _edgeStickSide = 0;
            _edgeStickTick = 0;
            return;
        }

        if (_edgeStickSide != side)
        {
            _edgeStickSide = side;
            _edgeStickTick = Environment.TickCount64;
        }
    }

    private bool EdgeStickReady(int side)
        => side != 0
           && _edgeStickSide == side
           && _edgeStickTick > 0
           && Environment.TickCount64 - _edgeStickTick >= EdgeStickAdmitMs;

    /// <summary>应用/对端同步后立即刷新穿边门控（不必等下一次鼠标移动）。</summary>
    public void RefreshEdgeCrossFromSettings() => SyncEdgeCrossMode();

    private void SyncEdgeCrossMode()
    {
        var mode = _settings.EdgeCrossMode switch
        {
            1 => EdgeCrossMode.DoubleTap,
            2 => EdgeCrossMode.SpeedBurst,
            3 => EdgeCrossMode.HotkeyOnly,
            _ => EdgeCrossMode.BarrierFree
        };
        _edgeGate.Mode = mode;
        if (_controllingRemote)
            _remoteCursor.EntryStickyEnabled = _edgeGate.UseEntrySticky;
    }

    /// <summary>
    /// 控制远端时虚拟光标出界：按远端机器在布局中的位置找下一台。
    /// 下一台是本机则收回控制；是其它电脑则切换过去（环形两端可互通）。
    /// </summary>
    private bool TryNavigateFromRemoteVirtual(int dx, int dy)
    {
        if (string.IsNullOrEmpty(_activePeer)) return false;

        var now = Environment.TickCount64;
        var (cx0, cy0) = _remoteCursor.Clamped01();

        // 粘滞进入边 + 过冲：避免入口微抖立刻甩回本机
        if (!_remoteCursor.TryGetExitDirection(dx, dy, _settings.TwoRow, out var dirH, out var dirVSide))
        {
            // 过冲积累中已算出界几何边，勿标成「内侧」，否则极速 inlandDepth 会被贴边坐标冲掉
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

        var (vx, vy) = _remoteCursor.Clamped01();
        if (IsLocalMachine(next))
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

        if (!_connections.IsPeerEdgeMouseCapable(next) || IsEdgeCrossExcluded?.Invoke(next) == true)
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
    /// 按物理布局找邻居，再按需改写为投影远程名（远程未具备穿边能力时保持电脑名）。
    /// </summary>
    private string? FindOccupiedNeighborOf(string?[] slots, int fromIndex, bool horizontal, int dir)
    {
        _ = slots;
        var physical = LayoutEdge.Neighbor(_settings, fromIndex, horizontal, dir);
        return physical == null ? null : ResolveEdgePeerNameForInput(physical);
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

    private async Task SendCompositionUpdateAsync(string peer, CompositionUpdatePayload payload)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.CompositionUpdate,
                MessageCodec.JsonBytes(payload)).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private async Task SendTextInputAsync(string peer, string text)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.TextInput,
                MessageCodec.JsonBytes(new TextInputPayload { Text = text })).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private async Task SendCompositionCancelAsync(string peer)
    {
        try
        {
            await _connections.SendToAsync(peer, MessageType.CompositionCancel, Array.Empty<byte>()).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _composition?.Dispose();
        _composition = null;
        ShowLocalCursor();
        Stop();
    }
}
