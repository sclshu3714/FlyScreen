using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FlyScreen.Models;
using FlyScreen.Native;
using FlyScreen.Services;
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
    /// <summary>本机最近一次物理键鼠时间；用于拒绝对端 SwitchFocus 抢光标。</summary>
    private long _lastPhysicalInputTick;
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

    public bool EasyMouseEnabled { get; set; } = true;
    public bool IsControllingRemote => _controllingRemote;
    /// <summary>是否接受对端键鼠注入（须先经 SwitchFocus 进入被控）。</summary>
    public bool IsReceivingRemoteControl => _receivingRemoteControl;
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
        // LL 钩子回调跑在安装线程；ShowCursor 计数也必须只在该线程调整
        _hookDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        EnsureCompositionHost();
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
    }

    /// <summary>对端 SwitchFocus 到本机：显示光标并进入「被远程控制」状态。</summary>
    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        // 已在被控：忽略重复 SwitchFocus，避免 AbsPointer 每次触控都把光标拽回入口点
        if (_receivingRemoteControl) return;

        // 本机正在操作：拒绝对端拽走光标（双向连接时对端误触边缘会发 SwitchFocus）
        if (!_controllingRemote
            && Environment.TickCount64 - _lastPhysicalInputTick < LocalActivityGuardMs)
            return;

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
        if (InputInjector.IsInjectedMouse(info.flags, info.dwExtraInfo))
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
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
            TryClaimPhysicalInput();
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
            _edgeGate.Observe(nx, ny, onEdge, Environment.TickCount64);

            if (EasyMouseEnabled && _settings.EasyMouse && !InSwitchCooldown())
            {
                if (TryDetectEdgePeer(info.pt.X, info.pt.Y, screenL, screenT, screenW, screenH,
                        out var peer, out var entryX, out var entryY, out var peerSide))
                {
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

        if (down)
            _lastPhysicalInputTick = Environment.TickCount64;

        if (down)
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

    private static bool IsCtrlDown()
        => (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0
           || (NativeMethods.GetAsyncKeyState(0xA2) & 0x8000) != 0
           || (NativeMethods.GetAsyncKeyState(0xA3) & 0x8000) != 0;

    private static bool IsAltDown()
        => (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0
           || (NativeMethods.GetAsyncKeyState(0xA4) & 0x8000) != 0
           || (NativeMethods.GetAsyncKeyState(0xA5) & 0x8000) != 0;

    private static bool IsWinDown()
        => (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0
           || (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0;

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
                if (IsLocalMachine(target))
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
        var idx = FindSlotIndex(slots, LocalPeerIdentity.MachineName);
        if (idx >= 0) return idx;
        var env = Environment.MachineName;
        if (!LocalPeerIdentity.IsBogusMachineName(env))
            return FindSlotIndex(slots, env);
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
            && _connections.IsPeerEdgeMouseCapable(name!);

        var nx = screenW <= 1 ? 0.5 : Math.Clamp((x - screenL) / (double)(screenW - 1), 0, 1);
        var ey = entryY;

        var now = Environment.TickCount64;

        // 左边缘 → 左侧机器
        if (x <= screenL && x <= _lastX)
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

        // 右边缘 → 右侧机器
        if (x >= right && x >= _lastX)
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

        // 双行：上/下边缘
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

    /// <summary>应用/对端同步后立即刷新穿边门控（不必等下一次鼠标移动）。</summary>
    public void RefreshEdgeCrossFromSettings() => SyncEdgeCrossMode();

    private void SyncEdgeCrossMode()
    {
        var mode = _settings.EdgeCrossMode switch
        {
            1 => EdgeCrossMode.DoubleTap,
            2 => EdgeCrossMode.SpeedBurst,
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
