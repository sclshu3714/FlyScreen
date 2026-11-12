using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FlyScreen.Input;
using FlyScreen.Models;
using FlyScreen.ScreenExtend;
using FlyScreen.Native;
using FlyScreen.Services;
using FlyScreen.Services.Clipboard;
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
    private int _savedLocalX;
    private int _savedLocalY;
    private bool _hasSavedLocalPos;
    private long _switchedToRemoteAtTick;
    private long _lastPhysicalInputTick;
    private readonly EdgeCrossGate _edgeGate = new();
    /// <summary>控远端时的虚拟光标（含进入边粘滞，防止误甩回本机）。</summary>
    private readonly EdgeMouseVirtualCursor _remoteCursor = new();
    /// <summary>对端相对本机：-1 左, +1 右, -2 上, +2 下。</summary>
    private int _peerSide;
    /// <summary>切换控制权后的短冷却，避免落在边缘时立刻被环形逻辑甩回。</summary>
    private long _switchCooldownUntilTick;
    private const int SwitchCooldownMs = 900;
    private long _edgeFileWaitStart;
    private bool _edgeDragSwallowLeftUp;

    /// <summary>按住文件拖过屏幕边缘时，把路径交给布局里的下一台。</summary>
    public EdgeFileDragService? EdgeFileDrag { get; set; }
    private const int LocalActivityGuardMs = 1000;
    private const int BriefRemoteMs = 450;
    /// <summary>贴边停留超过该毫秒则允许穿边（即使本帧没有继续外推）。</summary>
    private const int EdgeStickAdmitMs = 80;
    /// <summary>本机是否为当前活动电脑（显示操作光标的那一台）。</summary>
    private bool _isCursorOwner = true;
    private SynchronizationContext? _hookSync;
    private long _lastCursorOwnerBroadcastTick;
    /// <summary>本机最近一次按键/滚轮/点击；拒绝对端 SwitchFocus 只用这个（勿把鼠标微动当占用）。</summary>
    private long _lastLocalEngageTick;
    private int _edgeStickSide;
    private long _edgeStickTick;
    private long _lastEdgeDiagTick;
    private Thread? _hookThread;
    private int _hookThreadId;
    private readonly ManualResetEventSlim _hookReady = new(false);
    private readonly ConcurrentQueue<Action> _hookActions = new();
    public bool HooksInstalled => _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero;

    public bool EasyMouseEnabled { get; set; } = true;
    public bool IsControllingRemote => _controllingRemote;
    public bool IsReceivingRemoteControl => _receivingRemoteControl;
    /// <summary>光标是否在本机（用于剪贴板文件异步预取，勿在钩子里做重活）。</summary>
    public bool IsFocusOnLocalMachine => _focusOnLocal;
    public string? ActivePeer => _activePeer;

    public event Action<string?>? ControlTargetChanged;
    public event Action? RequestShowUi;

    /// <summary>本机 Ctrl+V / Shift+Insert：返回 true 表示已处理（吞键）。</summary>
    public Func<bool>? TryHandleLocalFilePasteHotkey { get; set; }
    /// <summary>扩展屏会话：排除该对端名的穿边切入。</summary>
    public Func<string, bool>? IsEdgeCrossExcluded { get; set; }
    /// <summary>扩展屏从机：暂停本机 EasyMouse。</summary>
    public Func<bool>? ShouldPauseEasyMouseForExtend { get; set; }

    public void SetEasyMousePaused(bool paused)
        => EasyMouseEnabled = !paused && _settings.EasyMouse;

    public MauiInputRouter(AppSettings settings, ConnectionManager connections)
    {
        _settings = settings;
        _connections = connections;
    }

    public void Start()
    {
        // WinUI 主线程忙碌时 LL 钩子会被系统静默卸掉 → 穿边「很难/不能」。
        // 独立 STA + GetMessage 泵，与 EdgeFileDragCapture 同套路。
        Stop();
        _hookReady.Reset();
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "FlyScreen.Maui.InputHooks"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        if (!_hookReady.Wait(5000))
            AppLog.Error("Maui-Win 键鼠钩子线程启动超时；穿边可能不可用");
    }

    public void Stop()
    {
        var thread = _hookThread;
        var tid = _hookThreadId;
        if (thread == null && _mouseHook == IntPtr.Zero && _keyHook == IntPtr.Zero)
            return;

        try
        {
            if (tid != 0)
                NativeMethods.PostThreadMessage(tid, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* ignore */ }

        if (thread != null && thread.IsAlive)
        {
            if (!thread.Join(2000))
                AppLog.Warn("Maui-Win 键鼠钩子线程未能及时退出");
        }

        _hookThread = null;
        _hookThreadId = 0;
        _hookSync = null;
        _mouseHook = IntPtr.Zero;
        _keyHook = IntPtr.Zero;
    }

    private void HookThreadMain()
    {
        _hookThreadId = Environment.CurrentManagedThreadId;
        _hookSync = new HookThreadSynchronizationContext(this);
        SynchronizationContext.SetSynchronizationContext(_hookSync);
        try
        {
            _mouseProc = MouseHookCallback;
            _keyProc = KeyHookCallback;
            // WH_MOUSE_LL 可用 NULL 模块句柄（本进程过程）
            var hMod = NativeMethods.GetModuleHandle(null);
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
            _keyHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyProc, hMod, 0);
            if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                AppLog.Error(
                    $"键鼠钩子安装失败 mouse={_mouseHook} key={_keyHook} win32={err} thread={_hookThreadId}；穿边将不可用（请以管理员运行）");
            }
            else
            {
                AppLog.Info($"键鼠钩子已安装（独立泵线程={_hookThreadId}）");
            }

            ShowLocalCursor();
            // 强制创建消息队列，否则启动瞬间 PostThreadMessage 会失败
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _hookReady.Set();

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == NativeMethods.WM_APP_HOOK_ACTION)
                    DrainHookActions();
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Maui-Win 键鼠钩子线程异常: " + ex.Message);
        }
        finally
        {
            try
            {
                while (NativeMethods.ShowCursor(true) < 0) { }
            }
            catch { /* ignore */ }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            if (_keyHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyHook);
                _keyHook = IntPtr.Zero;
            }

            _hookReady.Set();
        }
    }

    private void DrainHookActions()
    {
        while (_hookActions.TryDequeue(out var action))
        {
            try { action(); }
            catch { /* ignore */ }
        }
    }

    private void PostToHookThread(Action action)
    {
        _hookActions.Enqueue(action);
        var tid = _hookThreadId;
        if (tid != 0)
            NativeMethods.PostThreadMessage(tid, NativeMethods.WM_APP_HOOK_ACTION, IntPtr.Zero, IntPtr.Zero);
    }

    private sealed class HookThreadSynchronizationContext(MauiInputRouter owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
            => owner.PostToHookThread(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == owner._hookThreadId)
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim(false);
            Exception? error = null;
            owner.PostToHookThread(() =>
            {
                try { d(state); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            if (!done.Wait(500) || error != null)
            {
                // 超时则放弃；ShowCursor 偶发失败可接受
            }
        }
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
            NativeMethods.SetCursorPos(_savedLocalX, _savedLocalY);
            _lastX = _savedLocalX;
            _lastY = _savedLocalY;
        }
        else
        {
            var insetX = Math.Max(48, screenW / 8);
            var insetY = Math.Max(48, screenH / 8);
            var (vx, vy) = _remoteCursor.Clamped01();
            var px = screenL + screenW / 2;
            var py = screenT + (int)(Math.Clamp(vy, 0.05, 0.95) * Math.Max(1, screenH - 1));
            switch (side)
            {
                case -1:
                    px = screenL + insetX;
                    break;
                case 1:
                    px = screenL + screenW - insetX;
                    break;
                case -2:
                    px = screenL + (int)(Math.Clamp(vx, 0.05, 0.95) * Math.Max(1, screenW - 1));
                    py = screenT + insetY;
                    break;
                case 2:
                    px = screenL + (int)(Math.Clamp(vx, 0.05, 0.95) * Math.Max(1, screenW - 1));
                    py = screenT + screenH - insetY;
                    break;
            }

            NativeMethods.SetCursorPos(px, py);
            _lastX = px;
            _lastY = py;
        }

        BecomeCursorOwner(broadcast: true);
        ControlTargetChanged?.Invoke(null);
        EdgeFileDrag?.SetHover(null);
    }

    /// <summary>对端 SwitchFocus 到本机：显示光标并进入「被远程控制」状态。</summary>
    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        // 已在被控：忽略重复 SwitchFocus，避免 AbsPointer 每次触控都把光标拽回入口点
        if (_receivingRemoteControl) return;

        // 仅按键/点击/滚轮算「本机占用」；鼠标微动会让对端永远穿不进来
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
        BecomeCursorOwner(broadcast: false);
        MauiInputInjector.MoveAbsoluteNormalized(entryX, entryY);
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
        if (!_connections.IsPeerReachable(peer))
        {
            AppLog.Warn($"穿边中止：对端不可达 {peer}");
            return;
        }

        if (!_connections.PeerSupports(peer, PeerCaps.InjectInput))
        {
            AppLog.Warn($"穿边中止：对端无 InjectInput {peer}");
            return;
        }

        AppLog.Info($"穿边切换 → {peer} side={peerSide}");
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
        _isCursorOwner = false;
        PinLocalCursorForRemoteControl();
        ApplyInactiveCursorPresentation();
        _ = BroadcastCursorOwnerAsync(peer);

        ControlTargetChanged?.Invoke(peer);
        var (sx, sy) = _remoteCursor.Clamped01();
        _ = SendSwitchAsync(peer, sx, sy);
        EdgeFileDrag?.SetHover(peer);
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
        var injectedMouse = MauiInputInjector.IsInjectedMouse(info.flags, info.dwExtraInfo);
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
        else if (msg == NativeMethods.WM_MOUSEMOVE && !_controllingRemote)
        {
            TryClaimPhysicalInput();
        }

        if (!_controllingRemote && msg == NativeMethods.WM_MOUSEMOVE)
        {
            SyncEdgeCrossMode();
            var nxObs = screenW <= 1 ? 0.5 : Math.Clamp((info.pt.X - screenL) / (double)(screenW - 1), 0, 1);
            var nyObs = screenH <= 1 ? 0.5 : Math.Clamp((info.pt.Y - screenT) / (double)(screenH - 1), 0, 1);
            var rightObs = screenL + screenW - 1;
            var bottomObs = screenT + screenH - 1;
            var onEdge = info.pt.X <= screenL || info.pt.X >= rightObs || info.pt.Y <= screenT || info.pt.Y >= bottomObs;
            UpdateEdgeStick(info.pt.X, info.pt.Y, screenL, screenT, rightObs, bottomObs);
            _edgeGate.Observe(nxObs, nyObs, onEdge, Environment.TickCount64);

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

                MaybeLogEdgeBlocked(onEdge, pauseExtend);
            }
            else if (onEdge)
            {
                MaybeLogEdgeBlocked(onEdge, pauseExtend,
                    easyOff: !EasyMouseEnabled || !_settings.EasyMouse,
                    cooldown: InSwitchCooldown());
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
        if (MauiInputInjector.IsInjectedKey(info.flags, info.dwExtraInfo))
            return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;

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
        if (!_controllingRemote && down)
        {
            var vk = (int)info.vkCode;
            var isPaste = (vk == 0x56 && IsCtrlDown() && !IsAltDown() && !IsWinDown())
                          || (vk == 0x2D && IsShiftDown() && !IsCtrlDown() && !IsAltDown() && !IsWinDown());
            if (isPaste && TryHandleLocalFilePasteHotkey?.Invoke() == true)
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
    /// 退出集群 / 布局仅剩本机 / 断开前：结束远端控制态并强制显示本机光标。
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
    /// ShowCursor 计数按线程维护；必须在安装 LL 钩子的线程上调整。
    /// 钩子在独立泵线程时用其 SynchronizationContext.Send 短等。
    /// </summary>
    private void RunOnHookThread(Action action)
    {
        if (_hookThreadId != 0 && Environment.CurrentManagedThreadId == _hookThreadId)
        {
            action();
            return;
        }

        var sc = _hookSync;
        if (sc == null)
        {
            action();
            return;
        }

        try
        {
            sc.Send(_ =>
            {
                try { action(); }
                catch { /* ignore */ }
            }, null);
        }
        catch (NotSupportedException)
        {
            try { sc.Post(_ => { try { action(); } catch { /* ignore */ } }, null); }
            catch { /* ignore */ }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
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

    private void MaybeLogEdgeBlocked(bool onEdge, bool pauseExtend, bool easyOff = false, bool cooldown = false)
    {
        if (!onEdge) return;
        var now = Environment.TickCount64;
        if (now - _lastEdgeDiagTick < 1500) return;
        _lastEdgeDiagTick = now;
        var slots = GetLayoutSlots();
        var localIdx = FindLocalSlotIndex(slots);
        var side = _edgeStickSide;
        var neighbor = side == 0
            ? null
            : FindOccupiedNeighborOf(slots, localIdx, horizontal: side is -1 or 1, dir: side < 0 ? -1 : 1);
        AppLog.Info(
            $"穿边未切换 side={side} neighbor={neighbor ?? "(无)"} easyOff={easyOff} pauseExtend={pauseExtend} " +
            $"cooldown={cooldown} localIdx={localIdx} stickMs={Math.Max(0, now - _edgeStickTick)} hooks={HooksInstalled}");
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
                    VirtualScreen.GetBounds(out var l, out var t, out var w, out var h);
                    TakeLocalControl(l + w * 0.5, t + h * 0.5);
                }
                else
                    _ = TryHotkeySwitchToPeer(target);
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
            MainThread.BeginInvokeOnMainThread(MauiCloseToTray.RequestExit);
            return true;
        }

        if (MatchKey(vk, _settings.HotkeySettings))
        {
            RequestShowUi?.Invoke();
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
            MauiInputInjector.Key(new KeyEventPayload
            {
                VirtualKey = vk,
                Down = false,
                Extended = ext
            });
        }

        if (ModifierKeyRelease.IsPhysicallyDown(triggerVk))
        {
            MauiInputInjector.Key(new KeyEventPayload
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

        SwitchToPeer(machineName, 0.5, 0.5, peerSide: 0);
        return true;
    }

    /// <summary>热键切换到布局中的对端（含手机/平板：有 InjectInput 即可）。</summary>
    private bool TryHotkeySwitchToPeer(string peer)
    {
        if (!_connections.IsPeerReachable(peer))
        {
            AppLog.Warn($"热键切换失败：对端不可达 {peer}");
            return false;
        }

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

    private static bool IsCtrlDown() => ModifierKeyRelease.IsCtrlHeld();
    private static bool IsShiftDown() => ModifierKeyRelease.IsShiftHeld();
    private static bool IsAltDown() => ModifierKeyRelease.IsAltHeld();
    private static bool IsWinDown() => ModifierKeyRelease.IsWinHeld();

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

        // 继续外推，或已贴边停留足够久（Windows 夹住光标后常不再产生「更外」位移）
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

    /// <summary>应用/对端同步后立即刷新穿边门控。</summary>
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

    public void Dispose()
    {
        Stop();
        _hookReady.Dispose();
    }
}

