using FlyScreen.AppMaui.Services;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui;

public partial class PhoneTouchpadPage : ContentPage
{
    private const int WheelThresholdPx = 18;
    private const int WheelNotch = 120;
    private const int TapSlopPx = 12;
    private const double CursorSensitivity = 0.0022;
    /// <summary>非组字态下短暂空闲再发送；组字中绝不提交，等选词结束。</summary>
    private static readonly TimeSpan KeyboardCommitDelay = TimeSpan.FromMilliseconds(520);

    private readonly MauiAppHost _host;
    /// <summary>布局页传入的控制目标（非本机电脑名）。</summary>
    private readonly string? _targetPeer;
    private string? _activePeer;
    private bool _keyboardOpen;

    private Point? _padLast;
    private bool _padMoved;
    private double _cursorNx = 0.5;
    private double _cursorNy = 0.5;

    private Point? _wheelLast;
    private double _wheelAccY;
    private bool _wheelMoved;

    private long _clipboardSeq;
    private bool _suppressKeyboardSync;
    private bool _keyboardWasComposing;
    private CancellationTokenSource? _keyboardCommitCts;
    private long _lastClickTick;

    public PhoneTouchpadPage(string targetPeer)
    {
        InitializeComponent();
        _host = MauiAppHost.EnsureCreated();
        _targetPeer = string.IsNullOrWhiteSpace(targetPeer) ? null : targetPeer.Trim();
        _activePeer = _targetPeer;
        AttachPadGestures();
        AttachButtonZone(LeftZone, button: 0);
        AttachButtonZone(RightZone, button: 1);
        AttachWheelZone();
        KeyboardEntry.TextChanged += OnKeyboardTextChanged;
        KeyboardEntry.HandlerChanged += (_, _) => ApplyPhoneImeInputType();
        KeyboardEntry.Focused += (_, _) => ApplyPhoneImeInputType();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        _host.StatusMessage += OnHostStatus;
        _host.ScreenExtend.HostSessionReady += OnProjectionSessionChanged;
        _host.ScreenExtend.SessionEnded += OnProjectionSessionChanged;
        await _host.StartAsync();
        ResolveActivePeer();
        RefreshStatus();
        RefreshProjectButton();
        SetKeyboardOpen(false);
        // 触控板控制对端：屏蔽电脑穿透鼠标，避免双指针；本机外接鼠标照常
        _host.Platform.SetRemotePointerSuppressed(true);
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _host.StatusMessage -= OnHostStatus;
        _host.ScreenExtend.HostSessionReady -= OnProjectionSessionChanged;
        _host.ScreenExtend.SessionEnded -= OnProjectionSessionChanged;
        _keyboardCommitCts?.Cancel();
        _host.Platform.SetRemotePointerSuppressed(false);
    }

    private void OnProjectionSessionChanged()
        => MainThread.BeginInvokeOnMainThread(RefreshProjectButton);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _host.Platform.SetRemotePointerSuppressed(true);
        RefreshProjectButton();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // 返回主界面后允许电脑键鼠穿入本机
        _host.Platform.SetRemotePointerSuppressed(false);
    }

    private void OnHostStatus(string msg)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            // 保留控制目标提示；状态消息附加在后
            if (string.IsNullOrEmpty(_activePeer))
                StatusLabel.Text = msg;
            else if (!string.IsNullOrWhiteSpace(msg))
                StatusLabel.Text = $"控制目标：{_activePeer} · {msg}";
        });

    private void RefreshStatus()
    {
        StatusLabel.Text = _activePeer == null
            ? "未指定控制目标：请返回布局选中电脑后再打开触控板"
            : $"控制目标：{_activePeer}";
    }

    /// <summary>固定使用布局传入的目标；仅当其不可用时清空（不自动改选其它电脑）。</summary>
    private void ResolveActivePeer()
    {
        if (string.IsNullOrEmpty(_targetPeer))
        {
            _activePeer = null;
            return;
        }

        if (string.Equals(_targetPeer, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_targetPeer, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            _activePeer = null;
            return;
        }

        _activePeer = _targetPeer;
    }

    private async Task EnsureControlSessionAsync()
    {
        ResolveActivePeer();
        if (string.IsNullOrEmpty(_activePeer)) return;

        await _host.Connections.SendToAsync(_activePeer, MessageType.SwitchFocus,
            MessageCodec.JsonBytes(new SwitchFocusPayload
            {
                TargetMachine = _activePeer,
                EntryX = _cursorNx,
                EntryY = _cursorNy
            }));
        StatusLabel.Text = $"控制目标：{_activePeer}";
    }

    private void UpdateCursorDot(int dx, int dy)
    {
        _cursorNx = Math.Clamp(_cursorNx + dx * CursorSensitivity, 0, 1);
        _cursorNy = Math.Clamp(_cursorNy + dy * CursorSensitivity, 0, 1);
        if (CursorDot == null) return;
        AbsoluteLayout.SetLayoutBounds(CursorDot, new Rect(_cursorNx, _cursorNy, 14, 14));
        AbsoluteLayout.SetLayoutFlags(CursorDot, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.PositionProportional);
    }

    private void KeyboardToggle_Clicked(object? sender, EventArgs e)
        => SetKeyboardOpen(!_keyboardOpen);

    private void DismissKeyboard_Clicked(object? sender, EventArgs e)
        => SetKeyboardOpen(false);

    private void SetKeyboardOpen(bool open)
    {
        _keyboardOpen = open;
        KeyboardStrip.IsVisible = open;
        KeyboardToggleBtn.Style = (Style)Application.Current!.Resources[open ? "SegBtnActive" : "NormalBtn"];
        KeyboardToggleBtn.Text = open ? "键盘·开" : "键盘";

        if (open)
        {
            _suppressKeyboardSync = true;
            KeyboardEntry.Text = "";
            _suppressKeyboardSync = false;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), () =>
            {
                ApplyPhoneImeInputType();
                KeyboardEntry.Focus();
                ShowSoftKeyboard();
            });
            StatusLabel.Text = _activePeer == null
                ? "键盘已开：请先连接电脑"
                : $"键盘直发 → {_activePeer}（本地不保留）";
        }
        else
        {
            _keyboardCommitCts?.Cancel();
            KeyboardEntry.Unfocus();
            RefreshStatus();
        }
    }

    private void AttachPadGestures()
    {
        // Pan Completed（未滑动）→ 左键；另挂 Tap 作兜底（ClickAsync 有去抖防双发）
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPadPan;
        PadArea.GestureRecognizers.Add(pan);

        var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
        tap.Tapped += async (_, _) =>
        {
            if (_padMoved) return;
            await ClickAsync(0);
        };
        PadArea.GestureRecognizers.Add(tap);
    }

    private async void OnPadPan(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started)
        {
            _padLast = new Point(e.TotalX, e.TotalY);
            _padMoved = false;
            await EnsureControlSessionAsync();
        }
        else if (e.StatusType == GestureStatus.Running && _padLast is Point prev)
        {
            var dx = (int)Math.Round(e.TotalX - prev.X);
            var dy = (int)Math.Round(e.TotalY - prev.Y);
            _padLast = new Point(e.TotalX, e.TotalY);
            if (dx == 0 && dy == 0) return;
            if (Math.Abs(e.TotalX) > TapSlopPx || Math.Abs(e.TotalY) > TapSlopPx)
                _padMoved = true;

            UpdateCursorDot(dx, dy);
            ResolveActivePeer();
            if (string.IsNullOrEmpty(_activePeer)) return;
            await _host.Connections.SendToAsync(_activePeer, MessageType.MouseMove,
                MessageCodec.JsonBytes(new MouseMovePayload { Relative = true, Dx = dx, Dy = dy }));
        }
        else if (e.StatusType is GestureStatus.Completed or GestureStatus.Canceled)
        {
            // 未超过滑动阈值：整段手势视为触控板左键单击
            if (!_padMoved)
                await ClickAsync(0);
            _padLast = null;
            _padMoved = false;
        }
    }

    private void AttachButtonZone(Border zone, int button)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            FlashZone(zone);
            await ClickAsync(button);
        };
        zone.GestureRecognizers.Add(tap);
    }

    private void AttachWheelZone()
    {
        // Pan Completed（未滑动）→ 中键；另挂 Tap 兜底（去抖）
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnWheelPan;
        WheelZone.GestureRecognizers.Add(pan);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (_wheelMoved) return;
            FlashZone(WheelZone);
            await ClickAsync(2);
        };
        WheelZone.GestureRecognizers.Add(tap);
    }

    private async void OnWheelPan(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started)
        {
            _wheelLast = new Point(e.TotalX, e.TotalY);
            _wheelAccY = 0;
            _wheelMoved = false;
            await EnsureControlSessionAsync();
        }
        else if (e.StatusType == GestureStatus.Running && _wheelLast is Point prev)
        {
            var dy = e.TotalY - prev.Y;
            _wheelLast = new Point(e.TotalX, e.TotalY);
            if (Math.Abs(e.TotalY) > TapSlopPx || Math.Abs(e.TotalX) > TapSlopPx)
                _wheelMoved = true;

            _wheelAccY += dy;
            while (Math.Abs(_wheelAccY) >= WheelThresholdPx)
            {
                var up = _wheelAccY < 0;
                _wheelAccY += up ? WheelThresholdPx : -WheelThresholdPx;
                await WheelAsync(up ? WheelNotch : -WheelNotch);
            }
        }
        else if (e.StatusType is GestureStatus.Completed or GestureStatus.Canceled)
        {
            if (!_wheelMoved)
            {
                FlashZone(WheelZone);
                await ClickAsync(2);
            }
            _wheelLast = null;
            _wheelAccY = 0;
            _wheelMoved = false;
        }
    }

    private static async void FlashZone(Border zone)
    {
        var prev = zone.BackgroundColor;
        zone.BackgroundColor = Color.FromArgb("#DBEAFE");
        await Task.Delay(90);
        zone.BackgroundColor = prev;
    }

    private async Task WheelAsync(int delta)
    {
        await EnsureControlSessionAsync();
        if (string.IsNullOrEmpty(_activePeer)) return;
        await _host.Connections.SendToAsync(_activePeer, MessageType.MouseWheel,
            MessageCodec.JsonBytes(new MouseWheelPayload { Delta = delta }));
    }

    private async Task ClickAsync(int button)
    {
        // Pan Completed 与 Tap 可能连发：180ms 内同按钮只认一次
        var now = Environment.TickCount64;
        if (now - _lastClickTick < 180) return;
        _lastClickTick = now;

        await EnsureControlSessionAsync();
        if (string.IsNullOrEmpty(_activePeer)) return;
        await _host.Connections.SendToAsync(_activePeer, MessageType.MouseButton,
            MessageCodec.JsonBytes(new MouseButtonPayload { Button = button, Down = true }));
        await _host.Connections.SendToAsync(_activePeer, MessageType.MouseButton,
            MessageCodec.JsonBytes(new MouseButtonPayload { Button = button, Down = false }));
    }

    private void OnKeyboardTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressKeyboardSync || !_keyboardOpen) return;

        // 拼音组字中：绝不提交/清空，否则系统候选栏会被掐掉
        if (IsPhoneImeComposing())
        {
            _keyboardWasComposing = true;
            _keyboardCommitCts?.Cancel();
            return;
        }

        // 刚选完词：立刻发出，不必再等空闲
        if (_keyboardWasComposing)
        {
            _keyboardWasComposing = false;
            _keyboardCommitCts?.Cancel();
            _ = FlushKeyboardNowAsync();
            return;
        }

        _keyboardCommitCts?.Cancel();
        _keyboardCommitCts = new CancellationTokenSource();
        var ct = _keyboardCommitCts.Token;
        _ = CommitKeyboardAfterIdleAsync(ct);
    }

    private void KeyboardEntry_Completed(object? sender, EventArgs e)
        => _ = FlushKeyboardNowAsync();

    private async Task CommitKeyboardAfterIdleAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(KeyboardCommitDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 空闲结束时若仍在组字，继续等 TextChanged / Completed
        if (IsPhoneImeComposing())
            return;

        await FlushKeyboardNowAsync();
    }

    private async Task FlushKeyboardNowAsync()
    {
        if (!_keyboardOpen) return;
        if (IsPhoneImeComposing()) return;

        string text = "";
        await MainThread.InvokeOnMainThreadAsync(() => { text = KeyboardEntry.Text ?? ""; });
        if (string.IsNullOrEmpty(text)) return;

        await EnsureControlSessionAsync();
        if (string.IsNullOrEmpty(_activePeer)) return;

        await SendTextAsync(text);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _suppressKeyboardSync = true;
            KeyboardEntry.Text = "";
            _suppressKeyboardSync = false;
            ApplyPhoneImeInputType();
            KeyboardEntry.Focus();
            ShowSoftKeyboard();
        });
    }

    private void ApplyPhoneImeInputType()
    {
#if ANDROID
        try
        {
            if (KeyboardEntry.Handler?.PlatformView is not Android.Widget.EditText edit) return;
            edit.ShowSoftInputOnFocus = true;
            edit.InputType = Android.Text.InputTypes.ClassText;
            edit.ImeOptions = Android.Views.InputMethods.ImeAction.Done;
            edit.SetSingleLine(true);
            try { edit.PrivateImeOptions = null; } catch { /* ignore */ }
        }
        catch { /* ignore */ }
#endif
    }

    private bool IsPhoneImeComposing()
    {
#if ANDROID
        try
        {
            if (KeyboardEntry.Handler?.PlatformView is not Android.Widget.EditText edit) return false;
            if (edit.EditableText is not Android.Text.ISpannable span) return false;
            return Android.Views.InputMethods.BaseInputConnection.GetComposingSpanStart(span) >= 0;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

    private void ShowSoftKeyboard()
    {
#if ANDROID
        try
        {
            if (KeyboardEntry.Handler?.PlatformView is not Android.Widget.EditText edit) return;
            var imm = Android.App.Application.Context
                .GetSystemService(Android.Content.Context.InputMethodService)
                as Android.Views.InputMethods.InputMethodManager;
            imm?.ShowSoftInput(edit, Android.Views.InputMethods.ShowFlags.Implicit);
        }
        catch { /* ignore */ }
#endif
    }

    private async Task SendTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_activePeer)) return;

        if (text.All(IsSimpleKeyChar))
        {
            foreach (var ch in text)
                await SendCharAsKeyAsync(ch);
            return;
        }

        _clipboardSeq++;
        await _host.Connections.SendToAsync(_activePeer, MessageType.ClipboardText,
            MessageCodec.JsonBytes(new ClipboardTextPayload { Text = text, Sequence = _clipboardSeq }));
        await Task.Delay(40);
        await SendKeyStateAsync(0x11, down: true);
        await SendKeyStateAsync(0x56, down: true);
        await SendKeyStateAsync(0x56, down: false);
        await SendKeyStateAsync(0x11, down: false);
    }

    private static bool IsSimpleKeyChar(char ch)
        => ch is (>= ' ' and <= '~') or '\n' or '\r' or '\t';

    private async Task SendCharAsKeyAsync(char ch)
    {
        if (ch is '\n' or '\r')
        {
            await TapKeyAsync(0x0D);
            return;
        }

        if (ch == '\t')
        {
            await TapKeyAsync(0x09);
            return;
        }

        var vk = CharToVk(ch, out var shift);
        if (vk == 0)
        {
            _clipboardSeq++;
            await _host.Connections.SendToAsync(_activePeer!, MessageType.ClipboardText,
                MessageCodec.JsonBytes(new ClipboardTextPayload { Text = ch.ToString(), Sequence = _clipboardSeq }));
            await Task.Delay(40);
            await SendKeyStateAsync(0x11, down: true);
            await SendKeyStateAsync(0x56, down: true);
            await SendKeyStateAsync(0x56, down: false);
            await SendKeyStateAsync(0x11, down: false);
            return;
        }

        if (shift) await SendKeyStateAsync(0x10, down: true);
        await TapKeyAsync(vk);
        if (shift) await SendKeyStateAsync(0x10, down: false);
    }

    private static ushort CharToVk(char ch, out bool shift)
    {
        shift = false;
        if (ch == ' ') return 0x20;
        if (ch is >= 'a' and <= 'z') return (ushort)(ch - 'a' + 0x41);
        if (ch is >= 'A' and <= 'Z')
        {
            shift = true;
            return (ushort)ch;
        }

        if (ch is >= '0' and <= '9') return (ushort)ch;

        return ch switch
        {
            '!' => MarkShift(0x31, out shift),
            '@' => MarkShift(0x32, out shift),
            '#' => MarkShift(0x33, out shift),
            '$' => MarkShift(0x34, out shift),
            '%' => MarkShift(0x35, out shift),
            '^' => MarkShift(0x36, out shift),
            '&' => MarkShift(0x37, out shift),
            '*' => MarkShift(0x38, out shift),
            '(' => MarkShift(0x39, out shift),
            ')' => MarkShift(0x30, out shift),
            '-' => (ushort)0xBD,
            '_' => MarkShift(0xBD, out shift),
            '=' => (ushort)0xBB,
            '+' => MarkShift(0xBB, out shift),
            '[' => (ushort)0xDB,
            '{' => MarkShift(0xDB, out shift),
            ']' => (ushort)0xDD,
            '}' => MarkShift(0xDD, out shift),
            '\\' => (ushort)0xDC,
            '|' => MarkShift(0xDC, out shift),
            ';' => (ushort)0xBA,
            ':' => MarkShift(0xBA, out shift),
            '\'' => (ushort)0xDE,
            '"' => MarkShift(0xDE, out shift),
            ',' => (ushort)0xBC,
            '<' => MarkShift(0xBC, out shift),
            '.' => (ushort)0xBE,
            '>' => MarkShift(0xBE, out shift),
            '/' => (ushort)0xBF,
            '?' => MarkShift(0xBF, out shift),
            '`' => (ushort)0xC0,
            '~' => MarkShift(0xC0, out shift),
            _ => (ushort)0
        };

        static ushort MarkShift(ushort vk, out bool shift)
        {
            shift = true;
            return vk;
        }
    }

    private async Task TapKeyAsync(ushort vk)
    {
        await SendKeyStateAsync(vk, down: true);
        await SendKeyStateAsync(vk, down: false);
    }

    private async Task SendKeyStateAsync(ushort vk, bool down)
    {
        if (string.IsNullOrEmpty(_activePeer)) return;
        await _host.Connections.SendToAsync(_activePeer, MessageType.KeyEvent,
            MessageCodec.JsonBytes(new KeyEventPayload { VirtualKey = vk, Down = down }));
    }

    private async void Back_Clicked(object? sender, EventArgs e)
    {
        try
        {
            var nav = Shell.Current?.Navigation ?? Navigation;
            if (nav.NavigationStack.Count > 1)
                await nav.PopAsync(animated: true);
            else if (nav.ModalStack.Count > 0)
                await nav.PopModalAsync(animated: true);
        }
        catch { /* ignore */ }
    }

    private async void OpenProjection_Clicked(object? sender, EventArgs e)
    {
        try
        {
            if (_host.ScreenExtend.IsActive
                && _host.ScreenExtend.Role == FlyScreen.Services.ScreenExtend.ScreenExtendRole.Host)
            {
                await _host.StopProjectionAsync();
                RefreshProjectButton();
                return;
            }

            await Navigation.PushModalAsync(new ProjectionDialogPage(_host), animated: true);
            RefreshProjectButton();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"投影：{ex.Message}";
        }
    }

    private void RefreshProjectButton()
    {
        var on = _host.ScreenExtend.IsActive
                 && _host.ScreenExtend.Role == FlyScreen.Services.ScreenExtend.ScreenExtendRole.Host;
        ProjectBtn.Text = on ? "投影中" : "投影";
        ProjectBtn.Style = (Style)Application.Current!.Resources[on ? "SegBtnActive" : "NormalBtn"];
    }
}
