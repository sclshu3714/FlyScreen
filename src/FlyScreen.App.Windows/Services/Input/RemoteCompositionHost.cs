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

    public bool IsActive => !string.IsNullOrEmpty(_activePeer);
    public bool IsComposing => _composing;
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
        _dispatcher.BeginInvoke(() =>
        {
            EnsureUi();
            _activePeer = peer;
            ResetBox();
            FocusHost();
        });
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

    /// <summary>把物理键注入到宿主（钩子已吞掉原始按键）。</summary>
    public void FeedKey(ushort vk, bool down, bool extended)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsActive) return;
            EnsureUi();
            FocusHost();
            HideNativeImeUi();

            // 空格/数字选词：直接用已同步的候选提交，避免 TSF 确认事件丢导致手机不上屏
            if (down && TryCommitBySelectionKey(vk))
                return;

            InputInjector.Key(new KeyEventPayload { VirtualKey = vk, Down = down, Extended = extended });
            HideNativeImeUi();
        }, DispatcherPriority.Send);
    }

    /// <summary>1–9 / 空格 → 提交当前页候选；成功则不再把键交给 IME。</summary>
    private bool TryCommitBySelectionKey(ushort vk)
    {
        if (!_composing || _lastCandidates.Count == 0) return false;

        int index;
        if (vk == 0x20) // Space → 高亮项或首项
            index = _lastHighlight >= 0 && _lastHighlight < _lastCandidates.Count ? _lastHighlight : 0;
        else if (vk is >= 0x31 and <= 0x39) // '1'..'9'
            index = vk - 0x31;
        else if (vk is >= 0x61 and <= 0x69) // numpad 1..9
            index = vk - 0x61;
        else
            return false;

        if (index < 0 || index >= _lastCandidates.Count) return false;
        var text = _lastCandidates[index];
        if (string.IsNullOrEmpty(text)) return false;

        CancelImeCompositionSilent();
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
                ImmNotifyIME(himc, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
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

    /// <summary>把本机 IME 组字/候选窗赶到屏外（手机已自绘浮层）。</summary>
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
                CancelImeCompositionSilent();
                CommitTextDirect(text);
                return;
            }

            if (index == -10)
            {
                FeedKeyLocal(0xBD, true);  // VK_OEM_MINUS 翻页（微软拼音）
                FeedKeyLocal(0xBD, false);
                return;
            }

            if (index == -11)
            {
                FeedKeyLocal(0xBB, true);  // VK_OEM_PLUS
                FeedKeyLocal(0xBB, false);
                return;
            }

            if (index >= 0 && index < _lastCandidates.Count)
            {
                CancelImeCompositionSilent();
                CommitTextDirect(_lastCandidates[index]);
                return;
            }

            if (index >= 0 && index <= 8)
            {
                var digit = (ushort)(0x31 + index); // '1'..'9'
                FeedKeyLocal(digit, true);
                FeedKeyLocal(digit, false);
            }
        });
    }

    public void CancelComposition()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsActive) return;
            FocusHost();
            FeedKeyLocal(0x1B, true);  // Esc
            FeedKeyLocal(0x1B, false);
            var peer = _activePeer;
            _composing = false;
            ResetBox();
            if (!string.IsNullOrEmpty(peer))
                CompositionCancelled?.Invoke(peer);
        });
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
        => InputInjector.Key(new KeyEventPayload { VirtualKey = vk, Down = down });

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
            Width = 320,
            Height = 48,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = false, // 勿置顶，减少本机候选窗盖住其它应用
            AllowsTransparency = true,
            Opacity = 0.01, // 全 0 时部分 IME 不组字
            Background = System.Windows.Media.Brushes.White,
            Content = _box,
            Left = -32000,
            Top = -32000,
            ShowActivated = true
        };
        _window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            HideNativeImeUi();
        };
        _window.Show();
        HideNativeImeUi();
    }

    private void FocusHost()
    {
        if (_window == null || _box == null) return;
        if (!_window.IsVisible)
            _window.Show();
        try
        {
            _window.Activate();
            NativeMethodsFocus.ForceForeground(_hwnd);
            _box.Focus();
            Keyboard.Focus(_box);
        }
        catch
        {
            /* ignore */
        }
    }

    private void ResetBox()
    {
        if (_box == null) return;
        _suppressTextChanged = true;
        _box.Text = "";
        _suppressTextChanged = false;
        _lastReading = "";
        _lastCandidates = [];
        _lastHighlight = -1;
        _composing = false;
    }

    private void OnTextInputStart(object sender, TextCompositionEventArgs e)
    {
        _composing = true;
        HideNativeImeUi();
        PublishComposition(ResolveReading(e), TryReadCandidates());
    }

    private void OnTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        _composing = true;
        HideNativeImeUi();
        PublishComposition(ResolveReading(e), TryReadCandidates());
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        var text = e.TextComposition?.Text ?? "";
        _composing = false;
        HideNativeImeUi();
        if (!string.IsNullOrEmpty(text))
            CommitTextDirect(text);
        else
            PublishCancel();
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressTextChanged || _box == null || !IsActive) return;
        if (_composing) return;

        var text = _box.Text;
        if (string.IsNullOrEmpty(text)) return;

        // 英文直出 / IME 已写入结果但未走 TextInput 事件
        CommitTextDirect(text);
    }

    private string ResolveReading(TextCompositionEventArgs e)
    {
        // 优先 Imm 拼音读串；否则 WPF 中间串
        var imm = TryReadCompositionString();
        if (!string.IsNullOrEmpty(imm)) return imm;
        return e.TextComposition?.SystemText
               ?? e.TextComposition?.Text
               ?? "";
    }

    private void CommitTextDirect(string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_activePeer)) return;
        var peer = _activePeer;
        ResetBox();
        TextCommitted?.Invoke(peer, text);
    }

    private void PublishComposition(string reading, List<string> candidates)
    {
        if (string.IsNullOrEmpty(_activePeer)) return;
        reading ??= "";
        candidates ??= [];
        var highlight = TryReadSelectionIndex();
        if (highlight < 0 && candidates.Count > 0) highlight = 0;
        ReadCandidatePaging(out var numberBase, out var canPrev, out var canNext);
        if (reading == _lastReading
            && highlight == _lastHighlight
            && CandidatesEqual(candidates, _lastCandidates))
            return;

        _lastReading = reading;
        _lastCandidates = candidates;
        _lastHighlight = highlight;

        CompositionUpdated?.Invoke(_activePeer, new CompositionUpdatePayload
        {
            Reading = reading,
            Candidates = candidates,
            Highlight = highlight,
            NumberBase = numberBase,
            CanPagePrev = canPrev,
            CanPageNext = canNext
        });
    }

    private void ReadCandidatePaging(out int numberBase, out bool canPrev, out bool canNext)
    {
        numberBase = 1;
        canPrev = false;
        canNext = false;
        if (_hwnd == IntPtr.Zero) return;
        var himc = ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero) return;
        try
        {
            var size = ImmGetCandidateList(himc, 0, IntPtr.Zero, 0);
            if (size <= 0) return;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (ImmGetCandidateList(himc, 0, buf, size) <= 0) return;
                var header = Marshal.PtrToStructure<CANDIDATELIST>(buf);
                var pageStart = Math.Max(0, header.dwPageStart);
                var pageSize = header.dwPageSize > 0 ? header.dwPageSize : 9;
                numberBase = pageStart + 1;
                canPrev = pageStart > 0;
                canNext = pageStart + pageSize < header.dwCount;
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

    private void PublishCancel()
    {
        if (string.IsNullOrEmpty(_activePeer)) return;
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
            HideNativeImeUi();
            if (msg == WM_IME_NOTIFY)
            {
                var command = wParam.ToInt32();
                if (command is IMN_OPENCANDIDATE or IMN_CHANGECANDIDATE or IMN_CLOSECANDIDATE
                    || command == 0x0002 /* IMN_SETCONVERSIONMODE */)
                {
                    var reading = TryReadCompositionString();
                    var candidates = command == IMN_CLOSECANDIDATE ? [] : TryReadCandidates();
                    if (!string.IsNullOrEmpty(reading) || candidates.Count > 0)
                    {
                        _composing = true;
                        PublishComposition(reading, candidates);
                    }
                }
            }
            else
            {
                var reading = TryReadCompositionString();
                if (!string.IsNullOrEmpty(reading))
                {
                    _composing = true;
                    PublishComposition(reading, TryReadCandidates());
                }
            }
        }

        return IntPtr.Zero;
    }

    private string TryReadCompositionString()
    {
        if (_hwnd == IntPtr.Zero) return _lastReading;
        var himc = ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero) return _lastReading;
        try
        {
            // 拼音读串优先，便于手机浮层显示 xiang'yao...；否则组字中间汉字串
            var reading = ImmGetString(himc, GCS_COMPREADSTR);
            if (!string.IsNullOrEmpty(reading)) return reading;
            return ImmGetString(himc, GCS_COMPSTR);
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

    private List<string> TryReadCandidates()
    {
        var list = new List<string>();
        if (_hwnd == IntPtr.Zero) return list;
        var himc = ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero) return list;
        try
        {
            var size = ImmGetCandidateList(himc, 0, IntPtr.Zero, 0);
            if (size <= 0) return list;

            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (ImmGetCandidateList(himc, 0, buf, size) <= 0) return list;
                var header = Marshal.PtrToStructure<CANDIDATELIST>(buf);
                var pageStart = Math.Max(0, header.dwPageStart);
                var pageSize = header.dwPageSize > 0 ? header.dwPageSize : 9;
                var count = Math.Min(header.dwCount, pageStart + pageSize);
                for (var i = pageStart; i < count && list.Count < 9; i++)
                {
                    // CANDIDATELIST: 6 个 int 后接 dwOffset[dwCount]
                    var offsetPtr = IntPtr.Add(buf, 6 * 4 + i * 4);
                    var offset = Marshal.ReadInt32(offsetPtr);
                    if (offset <= 0 || offset >= size) continue;
                    var s = Marshal.PtrToStringUni(IntPtr.Add(buf, offset));
                    if (!string.IsNullOrEmpty(s))
                        list.Add(s);
                }
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

        return list;
    }

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
    private const int CPS_CANCEL = 0x0004;

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
    private static extern int ImmGetCandidateList(IntPtr hIMC, uint deIndex, IntPtr lpCandList, int dwBufLen);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCandidateWindow(IntPtr hIMC, ref CANDIDATEFORM lpCandidate);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref COMPOSITIONFORM lpCompForm);

    [DllImport("imm32.dll")]
    private static extern bool ImmNotifyIME(IntPtr hIMC, int dwAction, int dwIndex, int dwValue);
}

/// <summary>强制前置 IME 宿主窗口（跨线程焦点）。</summary>
file static class NativeMethodsFocus
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        var thisThread = GetCurrentThreadId();
        if (fgThread != thisThread)
            AttachThreadInput(thisThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (fgThread != thisThread)
                AttachThreadInput(thisThread, fgThread, false);
        }
    }
}
