using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FlyScreen.Services.Network;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace FlyScreen.Services.Input;

/// <summary>
/// 电脑侧隐藏 IME 宿主：控手机时在本机组字，把拼音/候选/确认文本发给手机置顶浮层。
/// </summary>
public sealed class RemoteCompositionHost : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private Window? _window;
    private WpfTextBox? _box;
    private HwndSource? _hwndSource;
    private string? _activePeer;
    private bool _composing;
    private bool _suppressTextChanged;
    private string _lastReading = "";
    private List<string> _lastCandidates = [];
    private int _lastHighlight = -1;
    private IntPtr _hwnd;
    private long _lastHideImeTick;
    private bool _focusOncePending;
    /// <summary>本机跟踪的拼音键缓冲（TSF 常拿不到 GCS_COMPREADSTR）。</summary>
    private readonly StringBuilder _latinBuffer = new();
    /// <summary>提交中：忽略 IME 二次 OnTextInput / TextChanged，防止重复上屏。</summary>
    private bool _committing;
    /// <summary>取消/强清 Imm 期间：忽略 Imm 回推的陈旧候选。</summary>
    private bool _suppressImePublish;
    /// <summary>Imm 候选快照（以电脑输入法为准；手机只镜像）。</summary>
    private List<string> _allCandidates = [];
    private int _highlightGlobal;
    private int _viewStart;
    private int _immPageSize = SinglePageSize;
    private bool _multiLine;
    /// <summary>单行页大小 / 多行时每行逻辑列数（数字键 1–N 作用域）。</summary>
    private const int SinglePageSize = 5;
    private const int MultiRows = 4;
    private static int MultiPageSize => SinglePageSize * MultiRows;

    public bool IsActive => !string.IsNullOrEmpty(_activePeer);
    public bool IsComposing => _composing;
    /// <summary>浮层应显示或方向键应进本机导航（缓冲/候选任一即可，不单靠 _composing）。</summary>
    public bool HasCandidateSession =>
        _composing || _latinBuffer.Length > 0 || _allCandidates.Count > 0 || !string.IsNullOrEmpty(_lastReading);
    public string? ActivePeer => _activePeer;

    public event Action<string, CompositionUpdatePayload>? CompositionUpdated;
    public event Action<string, string>? TextCommitted;
    public event Action<string>? CompositionCancelled;

    public RemoteCompositionHost(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Activate(string peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return;
        // 勿用 Send：切焦点时钩子可能仍在同一 UI 线程上
        _dispatcher.BeginInvoke(() =>
        {
            EnsureUi();
            _activePeer = peer;
            ResetBox();
            SoftFocusHost();
            HideNativeImeUiThrottled(force: true);
        }, DispatcherPriority.Normal);
    }

    public void Deactivate()
    {
        _dispatcher.BeginInvoke(() =>
        {
            var peer = _activePeer;
            _activePeer = null;
            if (_composing)
            {
                _composing = false;
                if (!string.IsNullOrEmpty(peer))
                    CompositionCancelled?.Invoke(peer);
            }
            ResetBox();
            _window?.Hide();
        });
    }

    /// <summary>
    /// 把物理键交给宿主。必须从钩子里尽快返回：
    /// 禁止在钩子回调里同步 ForceForeground / SendInput（同一 UI 线程会死锁卡死）。
    /// </summary>
    public void FeedKey(ushort vk, bool down, bool extended)
    {
        // Input 优先级：等当前钩子回调退出后再执行
        _dispatcher.BeginInvoke(() => FeedKeyCore(vk, down, extended), DispatcherPriority.Input);
    }

    private void FeedKeyCore(ushort vk, bool down, bool extended)
    {
        if (!IsActive || _committing) return;
        EnsureUi();

        // 空格/数字选词：直接用已同步的候选提交（不走 SendInput，避免再进钩子）
        if (down && TryCommitBySelectionKey(vk))
            return;

        // 方向键：本机候选导航（不要求 _composing，有缓冲/候选即导航）
        if (down && TryNavigateCandidates(vk))
        {
            _composing = true;
            return;
        }

        // 仅首次/失焦时轻量取焦，绝不 AttachThreadInput
        if (_focusOncePending || _box is { IsKeyboardFocused: false })
        {
            SoftFocusHost();
            _focusOncePending = false;
        }

        var chineseIme = IsChineseImeMode();

        // 英文/半角：不弹词条，字母直接进宿主并由 TextChanged → TextInput 上屏
        if (!chineseIme && !_composing)
        {
            if (down && vk is >= 0x41 and <= 0x5A)
                _latinBuffer.Clear(); // 防止残留拼音缓冲
            PostKeyToHost(vk, down, extended);
            return;
        }

        if (down)
            TrackLatinKey(vk);

        // PostMessage 到宿主 HWND，不经 SendInput，不会二次触发 LL 钩子
        PostKeyToHost(vk, down, extended);
        HideNativeImeUiThrottled(force: false);

        // 退格清空后立刻关掉手机浮层
        if (down && vk == 0x08 && _latinBuffer.Length == 0 && _allCandidates.Count == 0)
        {
            _composing = false;
            PublishCancel();
        }
        else if (down && _latinBuffer.Length > 0)
        {
            _composing = true;
            SyncCandidatesFromImm();
            PublishCurrentView();
        }
    }

    /// <summary>
    /// 方向/翻页：先驱动电脑 Imm（组字在 PC），再把 Imm 当前页镜像到手机。
    /// 只改手机浮层会导致永远只有 Imm 当前页那几个词。
    /// </summary>
    private bool TryNavigateCandidates(ushort vk)
    {
        if (vk is not (0x25 or 0x26 or 0x27 or 0x28 or 0x21 or 0x22))
            return false;

        if (_allCandidates.Count == 0)
        {
            SyncCandidatesFromImm();
            if (_allCandidates.Count == 0) return false;
        }

        SoftFocusHost();
        DriveImmNavigation(vk);
        // Imm 可能异步刷新；立即同步一拍，WndProc 还会再推
        SyncCandidatesFromImm();
        PublishCurrentView();
        // 再延后一拍，覆盖 TSF 晚到的候选页
        _dispatcher.BeginInvoke(() =>
        {
            if (_committing || _suppressImePublish) return;
            SyncCandidatesFromImm();
            PublishCurrentView();
        }, DispatcherPriority.Background);
        return true;
    }

    private void DriveImmNavigation(ushort vk)
    {
        var snap = TryReadImmSnapshot();
        var pageSize = snap.PageSize > 0 ? snap.PageSize : (_multiLine ? MultiPageSize : SinglePageSize);
        var pageStart = snap.PageStart;
        var sel = snap.Selection;
        var count = Math.Max(snap.Count, _allCandidates.Count);
        var pageEnd = pageStart + Math.Max(1, pageSize) - 1;
        if (count > 0) pageEnd = Math.Min(pageEnd, count - 1);
        var atPageEnd = sel >= pageEnd;
        var atPageStart = sel <= pageStart;

        switch (vk)
        {
            case 0x28: // Down：交给 Imm；页末时先推进 Imm 页起点
                if (atPageEnd)
                    ImmPageBy(+pageSize, pageStart, count);
                PulseKey(vk);
                break;

            case 0x26: // Up
                if (atPageStart && pageStart > 0)
                    ImmPageBy(-pageSize, pageStart, count);
                PulseKey(vk);
                break;

            case 0x27: // Right：页末展开多行（加大 Imm 页），否则 Imm 选中右移
                if (atPageEnd && !_multiLine)
                {
                    if (ImmSetCandidatePageSize(MultiPageSize))
                        _multiLine = true;
                    // 兼容微软拼音等：= 常用于候选扩展/翻页
                    PulseKey(0xBB);
                }
                else if (atPageEnd)
                    ImmPageBy(+pageSize, pageStart, count);
                PulseKey(vk);
                break;

            case 0x25: // Left：多行首页首项收回；否则 Imm 左移/上页
                if (_multiLine && atPageStart && pageStart == 0)
                {
                    if (ImmSetCandidatePageSize(SinglePageSize))
                        _multiLine = false;
                    PulseKey(0xBD);
                }
                else if (atPageStart && pageStart > 0)
                    ImmPageBy(-pageSize, pageStart, count);
                PulseKey(vk);
                break;

            case 0x22: // PageDown
                if (!_multiLine && pageStart + pageSize >= Math.Max(count, 1))
                {
                    if (ImmSetCandidatePageSize(MultiPageSize))
                        _multiLine = true;
                }
                else
                    ImmPageBy(+pageSize, pageStart, count);
                PulseKey(vk);
                break;

            case 0x21: // PageUp
                if (_multiLine && pageStart == 0)
                {
                    if (ImmSetCandidatePageSize(SinglePageSize))
                        _multiLine = false;
                }
                else
                    ImmPageBy(-pageSize, pageStart, count);
                PulseKey(vk);
                break;
        }
    }

    private void ImmPageBy(int delta, int pageStart, int count)
    {
        var pageSize = _multiLine ? MultiPageSize : SinglePageSize;
        var next = Math.Max(0, pageStart + delta);
        if (count > 0 && next >= count)
            next = Math.Max(0, ((count - 1) / pageSize) * pageSize);
        ImmSetCandidatePageStart(next);
        ImmSelectCandidate(next);
    }

    private void PulseKey(ushort vk)
    {
        var extended = vk is 0x21 or 0x22 or 0x25 or 0x26 or 0x27 or 0x28;
        PostKeyToHost(vk, true, extended);
        PostKeyToHost(vk, false, extended);
    }

    private void ImmSelectCandidate(int absoluteIndex)
    {
        if (absoluteIndex < 0) return;
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            var himc = ImmGetContext(_hwnd);
            if (himc == IntPtr.Zero) return;
            try
            {
                ImmNotifyIME(himc, NI_SELECTCANDIDATESTR, 0, absoluteIndex);
            }
            finally
            {
                ImmReleaseContext(_hwnd, himc);
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private bool ImmSetCandidatePageStart(int pageStart)
    {
        if (pageStart < 0) pageStart = 0;
        try
        {
            if (_hwnd == IntPtr.Zero) return false;
            var himc = ImmGetContext(_hwnd);
            if (himc == IntPtr.Zero) return false;
            try
            {
                return ImmNotifyIME(himc, NI_SETCANDIDATE_PAGESTART, 0, pageStart);
            }
            finally
            {
                ImmReleaseContext(_hwnd, himc);
            }
        }
        catch
        {
            return false;
        }
    }

    private bool ImmSetCandidatePageSize(int pageSize)
    {
        if (pageSize <= 0) return false;
        try
        {
            if (_hwnd == IntPtr.Zero) return false;
            var himc = ImmGetContext(_hwnd);
            if (himc == IntPtr.Zero) return false;
            try
            {
                var ok = ImmNotifyIME(himc, NI_SETCANDIDATE_PAGESIZE, 0, pageSize);
                if (ok) _immPageSize = pageSize;
                return ok;
            }
            finally
            {
                ImmReleaseContext(_hwnd, himc);
            }
        }
        catch
        {
            return false;
        }
    }

    private int CurrentPageSize() => _multiLine ? MultiPageSize : (_immPageSize > 0 ? _immPageSize : SinglePageSize);

    private void EnsureHighlightVisible()
    {
        var page = CurrentPageSize();
        if (page <= 0) page = SinglePageSize;
        if (_highlightGlobal < _viewStart)
            _viewStart = (_highlightGlobal / page) * page;
        else if (_highlightGlobal >= _viewStart + page)
            _viewStart = (_highlightGlobal / page) * page;
    }

    /// <summary>当前键盘是否处于中文等「本地语言」组字模式。</summary>
    private bool IsChineseImeMode()
    {
        if (_hwnd == IntPtr.Zero) return true;
        var himc = ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero) return true;
        try
        {
            if (!ImmGetConversionStatus(himc, out var conversion, out _))
                return true;
            // IME_CMODE_NATIVE = 0x0001：中文等；清零则为英文/半角直出
            return (conversion & 0x0001) != 0;
        }
        finally
        {
            ImmReleaseContext(_hwnd, himc);
        }
    }

    private void TrackLatinKey(ushort vk)
    {
        if (vk is >= 0x41 and <= 0x5A) // A-Z → 拼音小写
        {
            _latinBuffer.Append((char)(vk - 0x41 + 'a'));
            return;
        }

        if (vk == 0x08) // Backspace
        {
            if (_latinBuffer.Length > 0)
                _latinBuffer.Length--;
            return;
        }

        if (vk == 0x1B) // Esc
            _latinBuffer.Clear();
    }

    private void PostKeyToHost(ushort vk, bool down, bool extended)
    {
        if (_hwnd == IntPtr.Zero) return;
        var scan = MapVirtualKey(vk, 0);
        // lParam: repeat=1 | scan<<16 | (extended?1<<24) | (up: prev-down + transition)
        var lParam = 1u | (scan << 16);
        if (extended) lParam |= 1u << 24;
        if (!down) lParam |= (1u << 30) | (1u << 31);
        var msg = down ? 0x0100 /* WM_KEYDOWN */ : 0x0101 /* WM_KEYUP */;
        PostMessage(_hwnd, msg, (IntPtr)vk, (IntPtr)(int)lParam);
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>1–N / 空格 → 提交候选；多行时数字只选「当前行」内第 N 个（对齐桌面 IME）。</summary>
    private bool TryCommitBySelectionKey(ushort vk)
    {
        if (!_composing || _allCandidates.Count == 0) return false;

        int localInPage;
        if (vk == 0x20) // Space → 当前高亮
            localInPage = _highlightGlobal - _viewStart;
        else if (vk is >= 0x31 and <= 0x39) // '1'..'9'
        {
            var digit = vk - 0x31; // 0-based
            var cols = SinglePageSize;
            if (_multiLine)
            {
                // 数字键作用在高亮所在行：行首 + digit
                var rel = Math.Max(0, _highlightGlobal - _viewStart);
                var rowStart = rel / cols * cols;
                localInPage = rowStart + digit;
                if (digit >= cols) return false;
            }
            else
                localInPage = digit;
        }
        else if (vk is >= 0x61 and <= 0x69) // numpad 1..9
        {
            var digit = vk - 0x61;
            var cols = SinglePageSize;
            if (_multiLine)
            {
                var rel = Math.Max(0, _highlightGlobal - _viewStart);
                var rowStart = rel / cols * cols;
                localInPage = rowStart + digit;
                if (digit >= cols) return false;
            }
            else
                localInPage = digit;
        }
        else
            return false;

        var page = CurrentPageSize();
        if (localInPage < 0 || localInPage >= page) return false;
        var global = _viewStart + localInPage;
        if (global < 0 || global >= _allCandidates.Count) return false;
        var text = _allCandidates[global];
        if (string.IsNullOrEmpty(text)) return false;

        ImmSelectCandidate(global);
        CommitTextDirect(text);
        return true;
    }

    private void CancelImeCompositionSilent()
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            var himc = ImmGetContext(_hwnd);
            if (himc == IntPtr.Zero) return;
            try
            {
                // 清空组字串，避免提交后 Imm 仍挂着上一段拼音（zhongwen+shuru→中文输入）
                ImmSetCompositionStringW(himc, SCS_SETSTR, "", 0, "", 0);
                ImmNotifyIME(himc, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
                ImmNotifyIME(himc, NI_CLOSECANDIDATE, 0, 0);
            }
            finally
            {
                ImmReleaseContext(_hwnd, himc);
            }
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>提交后强清 Imm，并可选把本机拼音缓冲重新打进宿主（去掉残留前缀）。</summary>
    private void HardResetIme(bool refeedLatin)
    {
        CancelImeCompositionSilent();
        _allCandidates = [];
        _highlightGlobal = 0;
        _viewStart = 0;
        _multiLine = false;
        // Esc 再兜一层（部分 TSF 输入法忽略 ImmNotify）
        PostKeyToHost(0x1B, true, false);
        PostKeyToHost(0x1B, false, false);

        if (!refeedLatin || _latinBuffer.Length == 0) return;
        var letters = _latinBuffer.ToString();
        foreach (var ch in letters)
        {
            if (ch is < 'a' or > 'z') continue;
            var vk = (ushort)(ch - 'a' + 0x41);
            PostKeyToHost(vk, true, false);
            PostKeyToHost(vk, false, false);
        }
    }

    /// <summary>把本机 IME 组字/候选窗赶到屏外（限频，避免 Imm 调用拖死 UI）。</summary>
    private void HideNativeImeUiThrottled(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastHideImeTick < 80) return;
        _lastHideImeTick = now;
        HideNativeImeUi();
    }

    private void HideNativeImeUi()
    {
        if (_hwnd == IntPtr.Zero) return;
        var himc = ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero) return;
        try
        {
            var far = new POINT { X = -32000, Y = -32000 };
            var area = new RECT { Left = -32100, Top = -32100, Right = -32000, Bottom = -32000 };
            var cand = new CANDIDATEFORM
            {
                dwIndex = 0,
                dwStyle = CFS_EXCLUDE,
                ptCurrentPos = far,
                rcArea = area
            };
            ImmSetCandidateWindow(himc, ref cand);

            var comp = new COMPOSITIONFORM
            {
                dwStyle = CFS_POINT,
                ptCurrentPos = far,
                rcArea = area
            };
            ImmSetCompositionWindow(himc, ref comp);
        }
        finally
        {
            ImmReleaseContext(_hwnd, himc);
        }
    }

    public void SelectCandidate(int index, string? text)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsActive || _box == null) return;
            if (!string.IsNullOrEmpty(text))
            {
                CommitTextDirect(text);
                return;
            }

            if (index == -10)
            {
                DriveImmNavigation(0x21); // PageUp — 电脑 Imm 翻页
                SyncCandidatesFromImm();
                PublishCurrentView();
                return;
            }

            if (index == -11)
            {
                DriveImmNavigation(0x22); // PageDown
                SyncCandidatesFromImm();
                PublishCurrentView();
                return;
            }

            // -12：关闭多行 → 通知 Imm 收页
            if (index == -12)
            {
                ImmSetCandidatePageSize(SinglePageSize);
                _multiLine = false;
                PulseKey(0xBD); // -
                SyncCandidatesFromImm();
                PublishCurrentView();
                return;
            }

            if (index >= 0)
            {
                // 手机点选：相对当前 Imm 页；优先用绝对下标通知 Imm 再提交文本兜底
                var global = _viewStart + index;
                if (global >= 0 && global < _allCandidates.Count)
                {
                    ImmSelectCandidate(global);
                    CommitTextDirect(_allCandidates[global]);
                }
            }
        });
    }

    public void CancelComposition()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsActive) return;
            var peer = _activePeer;
            _suppressImePublish = true;
            _composing = false;
            try
            {
                // 强清 Imm，避免只藏浮层、下次输入仍带上次拼音/候选
                HardResetIme(refeedLatin: false);
                ResetBox();
            }
            finally
            {
                _dispatcher.BeginInvoke(() => { _suppressImePublish = false; }, DispatcherPriority.Background);
            }

            if (!string.IsNullOrEmpty(peer))
                CompositionCancelled?.Invoke(peer);
        }, DispatcherPriority.Input);
    }

    public void Dispose()
    {
        _dispatcher.BeginInvoke(() =>
        {
            _activePeer = null;
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }
            _window?.Close();
            _window = null;
            _box = null;
        });
    }

    private void FeedKeyLocal(ushort vk, bool down)
        => PostKeyToHost(vk, down, false);

    private void EnsureUi()
    {
        if (_window != null) return;

        _box = new WpfTextBox
        {
            FontSize = 16,
            AcceptsReturn = false,
            AcceptsTab = false,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
            Padding = new Thickness(4)
        };
        InputMethod.SetIsInputMethodEnabled(_box, true);
        InputMethod.SetPreferredImeState(_box, InputMethodState.On);

        TextCompositionManager.AddTextInputStartHandler(_box, OnTextInputStart);
        TextCompositionManager.AddTextInputUpdateHandler(_box, OnTextInputUpdate);
        TextCompositionManager.AddTextInputHandler(_box, OnTextInput);
        _box.TextChanged += OnTextChanged;

        _window = new Window
        {
            Title = "FlyScreen IME Host",
            Width = 8,
            Height = 8,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = false,
            AllowsTransparency = true,
            Opacity = 0.01,
            Background = System.Windows.Media.Brushes.White,
            Content = _box,
            Left = -32000,
            Top = -32000,
            ShowActivated = false // 创建时不抢焦点，避免切入控制时卡死
        };
        _window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            // 工具窗风格：少出现在 Alt+Tab
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            // 仅 TOOLWINDOW：少进 Alt+Tab；勿加 NOACTIVATE，否则 IME 收不到键
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex.ToInt64() | WS_EX_TOOLWINDOW));
            HideNativeImeUi();
        };
        _window.Show();
        _focusOncePending = true;
        HideNativeImeUi();
    }

    /// <summary>轻量聚焦：只用 WPF Focus，禁止 AttachThreadInput（钩子同线程会死锁）。</summary>
    private void SoftFocusHost()
    {
        if (_window == null || _box == null) return;
        try
        {
            if (!_window.IsVisible)
                _window.Show();
            // Activate 不调用 SetForegroundWindow，避免与 LL 钩子死锁
            _window.Activate();
            _box.Focus();
            Keyboard.Focus(_box);
        }
        catch
        {
            /* ignore */
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private void ResetBox()
    {
        if (_box == null) return;
        _suppressTextChanged = true;
        _box.Text = "";
        _suppressTextChanged = false;
        _latinBuffer.Clear();
        _allCandidates = [];
        _highlightGlobal = 0;
        _viewStart = 0;
        _immPageSize = SinglePageSize;
        _multiLine = false;
        _publishedMultiLine = false;
        _publishedViewStart = -1;
        _lastReading = "";
        _lastCandidates = [];
        _lastHighlight = -1;
        _composing = false;
    }

    private void OnTextInputStart(object sender, TextCompositionEventArgs e)
    {
        if (_committing) return;
        _composing = true;
        HideNativeImeUiThrottled(force: false);
        PublishComposition(ResolveReading(e), TryReadCandidates());
    }

    private void OnTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        if (_committing) return;
        _composing = true;
        HideNativeImeUiThrottled(force: false);
        PublishComposition(ResolveReading(e), TryReadCandidates());
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_committing) return;
        var text = e.TextComposition?.Text ?? "";
        _composing = false;
        HideNativeImeUiThrottled(force: false);
        if (!string.IsNullOrEmpty(text))
            CommitTextDirect(text);
        else
        {
            _latinBuffer.Clear();
            PublishCancel();
        }
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_committing || _suppressTextChanged || _box == null || !IsActive) return;
        if (_composing) return;

        var text = _box.Text;
        if (string.IsNullOrEmpty(text)) return;

        // 英文直出 / IME 已写入结果但未走 TextInput 事件
        CommitTextDirect(text);
    }

    private string ResolveReading(TextCompositionEventArgs e)
    {
        // 1) Imm 拼音读串（常带分隔符 zhong'wen）
        var immRead = TryReadImmString(GCS_COMPREADSTR);
        if (!string.IsNullOrEmpty(immRead) && LooksLikePinyin(immRead))
            return immRead;

        // 2) 本机按键缓冲（TSF 下 Imm 常空）
        if (_latinBuffer.Length > 0)
            return _latinBuffer.ToString();

        // 3) WPF / Imm 组字中间串
        var sys = e.TextComposition?.SystemText;
        if (!string.IsNullOrEmpty(sys) && LooksLikePinyin(sys))
            return sys;

        var immComp = TryReadImmString(GCS_COMPSTR);
        if (!string.IsNullOrEmpty(immComp) && LooksLikePinyin(immComp))
            return immComp;

        return e.TextComposition?.Text
               ?? immRead
               ?? immComp
               ?? "";
    }

    private static bool LooksLikePinyin(string s)
    {
        foreach (var ch in s)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '\'' or ' ')
                continue;
            // 含汉字则当作组字结果/中间汉字，不当拼音行
            if (ch > 0x7F) return false;
        }
        return s.Length > 0;
    }

    private void CommitTextDirect(string text)
    {
        if (_committing) return;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_activePeer)) return;

        _committing = true;
        _suppressImePublish = true;
        var peer = _activePeer;
        try
        {
            _suppressTextChanged = true;
            _composing = false;
            _latinBuffer.Clear();
            _allCandidates = [];
            _highlightGlobal = 0;
            _viewStart = 0;
            _multiLine = false;
            _publishedMultiLine = false;
            _publishedViewStart = -1;
            _lastReading = "";
            _lastCandidates = [];
            _lastHighlight = -1;
            if (_box != null)
                _box.Text = "";
            // 提交后必须清掉 Imm 组字上下文，否则下次 shuru 仍出「中文输入」
            HardResetIme(refeedLatin: false);
            TextCommitted?.Invoke(peer, text);
            CompositionCancelled?.Invoke(peer);
        }
        finally
        {
            _suppressTextChanged = false;
            // 延后解除，吞掉 CancelIme 触发的迟滞 TextInput / 陈旧候选
            _dispatcher.BeginInvoke(() =>
            {
                _committing = false;
                _suppressImePublish = false;
            }, DispatcherPriority.Background);
        }
    }

    private void SyncCandidatesFromImm()
    {
        // Imm 读串仍带着上一段前缀（提交未清干净）→ 强清并重打本机缓冲
        if (_latinBuffer.Length > 0 && ImmReadingHasStalePrefix())
        {
            HardResetIme(refeedLatin: true);
            _allCandidates = [];
            _dispatcher.BeginInvoke(() =>
            {
                if (_committing || _latinBuffer.Length == 0) return;
                ApplyImmSnapshot(TryReadImmSnapshot());
                PublishCurrentView();
            }, DispatcherPriority.Input);
            return;
        }

        ApplyImmSnapshot(TryReadImmSnapshot());
    }

    /// <summary>用电脑 Imm 的页窗/选中/列表覆盖本机视图状态。</summary>
    private void ApplyImmSnapshot(ImmCandidateSnapshot snap)
    {
        _allCandidates = snap.All;
        if (_allCandidates.Count == 0)
        {
            _highlightGlobal = 0;
            _viewStart = 0;
            return;
        }

        // Imm 若只返回「当前页」字符串（dwPageStart 常为 0、dwCount≈页大小），整表即当前页
        var pageSize = snap.PageSize > 0 ? snap.PageSize : _allCandidates.Count;
        _immPageSize = pageSize;
        if (pageSize > SinglePageSize)
            _multiLine = true;

        if (snap.PageStart > 0 || snap.Count > pageSize)
        {
            // 全量列表 + 页窗
            _viewStart = Math.Clamp(snap.PageStart, 0, Math.Max(0, _allCandidates.Count - 1));
            _highlightGlobal = Math.Clamp(snap.Selection, 0, _allCandidates.Count - 1);
        }
        else
        {
            // 仅当前页：保持 _viewStart 逻辑页号用 numberBase；选中用 Imm dwSelection
            _viewStart = 0;
            _highlightGlobal = Math.Clamp(snap.Selection, 0, _allCandidates.Count - 1);
        }

        EnsureHighlightVisible();
    }

    private sealed class ImmCandidateSnapshot
    {
        public List<string> All { get; init; } = [];
        public int PageStart { get; init; }
        public int PageSize { get; init; }
        public int Selection { get; init; }
        public int Count { get; init; }
    }

    private ImmCandidateSnapshot TryReadImmSnapshot()
    {
        var empty = new ImmCandidateSnapshot();
        if (_hwnd == IntPtr.Zero) return empty;
        var himc = ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero) return empty;
        try
        {
            var size = ImmGetCandidateList(himc, 0, IntPtr.Zero, 0);
            if (size <= 0) return empty;

            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (ImmGetCandidateList(himc, 0, buf, size) <= 0) return empty;
                var header = Marshal.PtrToStructure<CANDIDATELIST>(buf);
                var list = new List<string>();
                for (var i = 0; i < header.dwCount && list.Count < 128; i++)
                {
                    var offsetPtr = IntPtr.Add(buf, 6 * 4 + i * 4);
                    var offset = Marshal.ReadInt32(offsetPtr);
                    if (offset <= 0 || offset >= size) continue;
                    var s = Marshal.PtrToStringUni(IntPtr.Add(buf, offset));
                    if (!string.IsNullOrEmpty(s))
                        list.Add(s);
                }

                return new ImmCandidateSnapshot
                {
                    All = list,
                    PageStart = Math.Max(0, header.dwPageStart),
                    PageSize = header.dwPageSize > 0 ? header.dwPageSize : list.Count,
                    Selection = Math.Max(0, header.dwSelection),
                    Count = header.dwCount
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            ImmReleaseContext(_hwnd, himc);
        }
    }

    /// <summary>
    /// Imm 拼音与本机缓冲不一致：
    /// - Imm 更长且以本机结尾（zhongwen+shuru）
    /// - 或两者完全分叉（Imm 仍停在 zhongwen，本机已是 shuru）
    /// </summary>
    private bool ImmReadingHasStalePrefix()
    {
        var latNorm = NormalizePinyin(_latinBuffer.ToString());
        if (latNorm.Length == 0) return false;

        var imm = TryReadImmString(GCS_COMPREADSTR);
        if (string.IsNullOrEmpty(imm) || !LooksLikePinyin(imm))
            imm = TryReadImmString(GCS_COMPSTR);
        if (string.IsNullOrEmpty(imm) || !LooksLikePinyin(imm))
            return false;

        var immNorm = NormalizePinyin(imm);
        if (immNorm.Equals(latNorm, StringComparison.Ordinal)) return false;
        // Imm 落后于按键（正常组字中）
        if (latNorm.StartsWith(immNorm, StringComparison.Ordinal)) return false;
        // 其余：残留前缀或完全分叉 → 需强清
        return true;
    }

    private static string NormalizePinyin(string s)
        => s.Replace("'", "").Replace(" ", "").ToLowerInvariant();

    private void PublishCurrentView()
    {
        if (_committing || string.IsNullOrEmpty(_activePeer)) return;

        var reading = PreferPinyinReading();
        if (_latinBuffer.Length > 0)
            reading = _latinBuffer.ToString();
        reading = PinyinReadingFormatter.Format(reading);

        if (string.IsNullOrEmpty(reading) && _allCandidates.Count == 0)
        {
            if (_composing || !string.IsNullOrEmpty(_lastReading) || _lastCandidates.Count > 0)
            {
                _composing = false;
                PublishCancel();
            }
            return;
        }

        var page = CurrentPageSize();
        EnsureHighlightVisible();
        // 有全量列表时按 Imm 页窗切片；仅当前页时整表推送
        List<string> slice;
        int highlight;
        int numberBase;
        if (_viewStart > 0 || _allCandidates.Count > page)
        {
            slice = _allCandidates.Skip(_viewStart).Take(page).ToList();
            highlight = slice.Count == 0 ? -1 : Math.Clamp(_highlightGlobal - _viewStart, 0, slice.Count - 1);
            numberBase = _viewStart + 1;
        }
        else
        {
            slice = _allCandidates.Take(page).ToList();
            highlight = slice.Count == 0 ? -1 : Math.Clamp(_highlightGlobal, 0, slice.Count - 1);
            numberBase = 1;
        }

        var canPrev = _viewStart > 0 || _multiLine;
        var canNext = _viewStart + page < _allCandidates.Count || (!_multiLine && _allCandidates.Count > 0);

        if (reading == _lastReading
            && highlight == _lastHighlight
            && CandidatesEqual(slice, _lastCandidates)
            && _publishedMultiLine == _multiLine
            && _publishedViewStart == _viewStart)
            return;

        _lastReading = reading;
        _lastCandidates = slice;
        _lastHighlight = highlight;
        _publishedMultiLine = _multiLine;
        _publishedViewStart = _viewStart;

        CompositionUpdated?.Invoke(_activePeer, new CompositionUpdatePayload
        {
            Reading = reading,
            Candidates = slice,
            Highlight = highlight,
            // 单行：1..N；多行：手机按 GridColumns 只给当前行标 1..N
            NumberBase = _multiLine ? 1 : numberBase,
            CanPagePrev = canPrev,
            CanPageNext = canNext,
            MultiLine = _multiLine,
            // 逻辑列数（与单行页大小一致）；手机纵向展示按此分行
            GridColumns = SinglePageSize
        });
    }

    private bool _publishedMultiLine;
    private int _publishedViewStart = -1;

    /// <summary>兼容旧调用：刷新 Imm 后发布当前视图。</summary>
    private void PublishComposition(string reading, List<string> candidates)
    {
        _ = reading;
        _ = candidates;
        SyncCandidatesFromImm();
        PublishCurrentView();
    }

    private void PublishCancel()
    {
        if (string.IsNullOrEmpty(_activePeer)) return;
        _latinBuffer.Clear();
        _allCandidates = [];
        _highlightGlobal = 0;
        _viewStart = 0;
        _multiLine = false;
        _publishedMultiLine = false;
        _publishedViewStart = -1;
        _lastReading = "";
        _lastCandidates = [];
        _lastHighlight = -1;
        CompositionCancelled?.Invoke(_activePeer);
    }

    private static bool CandidatesEqual(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_IME_NOTIFY / WM_IME_COMPOSITION：刷新候选
        const int WM_IME_COMPOSITION = 0x010F;
        const int WM_IME_NOTIFY = 0x0282;
        const int IMN_OPENCANDIDATE = 0x0005;
        const int IMN_CHANGECANDIDATE = 0x0003;
        const int IMN_CLOSECANDIDATE = 0x0004;

        if (msg == WM_IME_COMPOSITION || msg == WM_IME_NOTIFY)
        {
            HideNativeImeUiThrottled(force: false);
            if (msg == WM_IME_NOTIFY)
            {
                var command = wParam.ToInt32();
                if (command is IMN_OPENCANDIDATE or IMN_CHANGECANDIDATE or IMN_CLOSECANDIDATE
                    || command == 0x0002 /* IMN_SETCONVERSIONMODE */)
                {
                    if (_committing || _suppressImePublish) return IntPtr.Zero;
                    var reading = PreferPinyinReading();
                    var candidates = command == IMN_CLOSECANDIDATE ? [] : TryReadCandidates();
                    if (command == IMN_CLOSECANDIDATE
                        && string.IsNullOrEmpty(reading)
                        && candidates.Count == 0
                        && _latinBuffer.Length == 0)
                    {
                        _composing = false;
                        PublishCancel();
                    }
                    else
                    {
                        _composing = true;
                        PublishComposition(reading, candidates);
                    }
                }
            }
            else
            {
                if (_committing || _suppressImePublish) return IntPtr.Zero;
                var reading = PreferPinyinReading();
                _composing = true;
                PublishComposition(reading, TryReadCandidates());
            }
        }

        return IntPtr.Zero;
    }

    private string PreferPinyinReading()
    {
        // 按键缓冲最稳；Imm 读串有分隔符时更优
        var immRead = TryReadImmString(GCS_COMPREADSTR);
        if (!string.IsNullOrEmpty(immRead) && LooksLikePinyin(immRead))
            return immRead;
        if (_latinBuffer.Length > 0)
            return _latinBuffer.ToString();
        var immComp = TryReadImmString(GCS_COMPSTR);
        if (!string.IsNullOrEmpty(immComp) && LooksLikePinyin(immComp))
            return immComp;
        return "";
    }

    private string TryReadImmString(int index)
    {
        if (_hwnd == IntPtr.Zero) return "";
        var himc = ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero) return "";
        try
        {
            return ImmGetString(himc, index);
        }
        finally
        {
            ImmReleaseContext(_hwnd, himc);
        }
    }

    private int TryReadSelectionIndex()
    {
        if (_hwnd == IntPtr.Zero) return -1;
        var himc = ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero) return -1;
        try
        {
            var size = ImmGetCandidateList(himc, 0, IntPtr.Zero, 0);
            if (size <= 0) return -1;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (ImmGetCandidateList(himc, 0, buf, size) <= 0) return -1;
                var header = Marshal.PtrToStructure<CANDIDATELIST>(buf);
                var pageStart = Math.Max(0, header.dwPageStart);
                var sel = header.dwSelection - pageStart;
                return sel >= 0 ? sel : 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            ImmReleaseContext(_hwnd, himc);
        }
    }

    private List<string> TryReadCandidates() => TryReadImmSnapshot().All;

    private List<string> TryReadAllCandidates() => TryReadImmSnapshot().All;

    private static string ImmGetString(IntPtr himc, int index)
    {
        var bytes = ImmGetCompositionStringW(himc, index, null, 0);
        if (bytes <= 0) return "";
        var buf = new byte[bytes];
        ImmGetCompositionStringW(himc, index, buf, bytes);
        return Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    private const int GCS_COMPSTR = 0x0008;
    private const int GCS_COMPREADSTR = 0x0001;
    private const int CFS_POINT = 0x0002;
    private const int CFS_EXCLUDE = 0x0080;
    private const int NI_COMPOSITIONSTR = 0x0015;
    private const int NI_CLOSECANDIDATE = 0x0011;
    private const int NI_SELECTCANDIDATESTR = 0x0017;
    private const int NI_SETCANDIDATE_PAGESTART = 0x0018;
    private const int NI_SETCANDIDATE_PAGESIZE = 0x0019;
    private const int CPS_CANCEL = 0x0004;
    private const int SCS_SETSTR = 0x0009;

    [StructLayout(LayoutKind.Sequential)]
    private struct CANDIDATELIST
    {
        public int dwSize;
        public int dwStyle;
        public int dwCount;
        public int dwSelection;
        public int dwPageStart;
        public int dwPageSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CANDIDATEFORM
    {
        public int dwIndex;
        public int dwStyle;
        public POINT ptCurrentPos;
        public RECT rcArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMPOSITIONFORM
    {
        public int dwStyle;
        public POINT ptCurrentPos;
        public RECT rcArea;
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex, byte[]? lpBuf, int dwBufLen);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ImmSetCompositionStringW(
        IntPtr hIMC, int dwIndex, string? lpComp, int dwCompLen, string? lpRead, int dwReadLen);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCandidateList(IntPtr hIMC, uint deIndex, IntPtr lpCandList, int dwBufLen);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCandidateWindow(IntPtr hIMC, ref CANDIDATEFORM lpCandidate);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref COMPOSITIONFORM lpCompForm);

    [DllImport("imm32.dll")]
    private static extern bool ImmNotifyIME(IntPtr hIMC, int dwAction, int dwIndex, int dwValue);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetConversionStatus(IntPtr hIMC, out int lpfdwConversion, out int lpfdwSentence);
}
