using FlyScreen.AppMaui.Services;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui;

public partial class PhoneTouchpadPage : ContentPage
{
    private const int WheelThresholdPx = 18;
    private const int WheelNotch = 120;
    private const int TapSlopPx = 12;

    private readonly MauiAppHost _host;
    private string? _activePeer;
    private bool _keyboardMode;

    private Point? _padLast;
    private bool _padMoved;

    private Point? _wheelLast;
    private double _wheelAccY;
    private bool _wheelMoved;

    private string _keyboardPrev = "";
    private long _clipboardSeq;
    private bool _suppressKeyboardSync;

    public PhoneTouchpadPage()
    {
        InitializeComponent();
        _host = MauiAppHost.EnsureCreated();
        AttachPadGestures();
        AttachButtonZone(LeftZone, button: 0);
        AttachButtonZone(RightZone, button: 1);
        AttachWheelZone();
        KeyboardEditor.TextChanged += OnKeyboardTextChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        _host.StatusMessage += msg => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = msg);
        await _host.StartAsync();
        PickPeer();
        RefreshStatus();
        ApplyInputMode(keyboard: false);
    }

    private void RefreshStatus()
    {
        StatusLabel.Text = _activePeer == null
            ? "未连接对端：请先在设置中配置密钥并连接"
            : $"控制目标：{_activePeer}";
    }

    private void PickPeer()
    {
        _activePeer = _host.Connections.GetConfiguredPeerStatuses()
            .FirstOrDefault(s => s.AnyAlive)?.Name
            ?? _host.Settings.MachineSlots.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s)
                && !string.Equals(s, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(s, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
    }

    private void ModePad_Clicked(object? sender, EventArgs e) => ApplyInputMode(keyboard: false);
    private void ModeKey_Clicked(object? sender, EventArgs e) => ApplyInputMode(keyboard: true);

    private void ApplyInputMode(bool keyboard)
    {
        _keyboardMode = keyboard;
        PadArea.IsVisible = !keyboard;
        KeyboardArea.IsVisible = keyboard;

        var active = (Style)Application.Current!.Resources["SegBtnActive"];
        var normal = (Style)Application.Current!.Resources["SegBtn"];
        ModePadBtn.Style = keyboard ? normal : active;
        ModeKeyBtn.Style = keyboard ? active : normal;

        if (keyboard)
        {
            _suppressKeyboardSync = true;
            KeyboardEditor.Text = _keyboardPrev;
            _suppressKeyboardSync = false;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), () => KeyboardEditor.Focus());
        }
        else
        {
            KeyboardEditor.Unfocus();
        }
    }

    private void AttachPadGestures()
    {
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
        if (_keyboardMode) return;

        if (e.StatusType == GestureStatus.Started)
        {
            _padLast = new Point(e.TotalX, e.TotalY);
            _padMoved = false;
        }
        else if (e.StatusType == GestureStatus.Running && _padLast is Point prev)
        {
            var dx = (int)Math.Round(e.TotalX - prev.X);
            var dy = (int)Math.Round(e.TotalY - prev.Y);
            _padLast = new Point(e.TotalX, e.TotalY);
            if (dx == 0 && dy == 0) return;
            if (Math.Abs(e.TotalX) > TapSlopPx || Math.Abs(e.TotalY) > TapSlopPx)
                _padMoved = true;

            PickPeer();
            if (string.IsNullOrEmpty(_activePeer)) return;
            await _host.Connections.SendToAsync(_activePeer, MessageType.MouseMove,
                MessageCodec.JsonBytes(new MouseMovePayload { Relative = true, Dx = dx, Dy = dy }));
        }
        else if (e.StatusType is GestureStatus.Completed or GestureStatus.Canceled)
            _padLast = null;
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
                // 上滑 → 滚轮上（正 Delta）；下滑 → 滚轮下（负 Delta）
                await WheelAsync(up ? WheelNotch : -WheelNotch);
            }
        }
        else if (e.StatusType is GestureStatus.Completed or GestureStatus.Canceled)
        {
            _wheelLast = null;
            _wheelAccY = 0;
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
        PickPeer();
        if (string.IsNullOrEmpty(_activePeer)) return;
        await _host.Connections.SendToAsync(_activePeer, MessageType.MouseWheel,
            MessageCodec.JsonBytes(new MouseWheelPayload { Delta = delta }));
    }

    private async Task ClickAsync(int button)
    {
        PickPeer();
        if (string.IsNullOrEmpty(_activePeer)) return;
        await _host.Connections.SendToAsync(_activePeer, MessageType.MouseButton,
            MessageCodec.JsonBytes(new MouseButtonPayload { Button = button, Down = true }));
        await _host.Connections.SendToAsync(_activePeer, MessageType.MouseButton,
            MessageCodec.JsonBytes(new MouseButtonPayload { Button = button, Down = false }));
    }

    private async void OnKeyboardTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressKeyboardSync || !_keyboardMode) return;

        var next = e.NewTextValue ?? "";
        var prev = _keyboardPrev;
        _keyboardPrev = next;

        PickPeer();
        if (string.IsNullOrEmpty(_activePeer)) return;

        if (next.Length < prev.Length && prev.StartsWith(next, StringComparison.Ordinal))
        {
            var removed = prev.Length - next.Length;
            for (var i = 0; i < removed; i++)
                await TapKeyAsync(0x08);
            return;
        }

        if (next.Length > prev.Length && next.StartsWith(prev, StringComparison.Ordinal))
        {
            await SendTextAsync(next[prev.Length..]);
            return;
        }

        if (next.Length == 0 && prev.Length > 0)
        {
            for (var i = 0; i < prev.Length; i++)
                await TapKeyAsync(0x08);
        }
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

        // 中文等：写入对端剪贴板并 Ctrl+V 粘贴
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

        switch (ch)
        {
            case '!': shift = true; return 0x31;
            case '@': shift = true; return 0x32;
            case '#': shift = true; return 0x33;
            case '$': shift = true; return 0x34;
            case '%': shift = true; return 0x35;
            case '^': shift = true; return 0x36;
            case '&': shift = true; return 0x37;
            case '*': shift = true; return 0x38;
            case '(': shift = true; return 0x39;
            case ')': shift = true; return 0x30;
            case '-': return 0xBD;
            case '_': shift = true; return 0xBD;
            case '=': return 0xBB;
            case '+': shift = true; return 0xBB;
            case '[': return 0xDB;
            case '{': shift = true; return 0xDB;
            case ']': return 0xDD;
            case '}': shift = true; return 0xDD;
            case '\\': return 0xDC;
            case '|': shift = true; return 0xDC;
            case ';': return 0xBA;
            case ':': shift = true; return 0xBA;
            case '\'': return 0xDE;
            case '"': shift = true; return 0xDE;
            case ',': return 0xBC;
            case '<': shift = true; return 0xBC;
            case '.': return 0xBE;
            case '>': shift = true; return 0xBE;
            case '/': return 0xBF;
            case '?': shift = true; return 0xBF;
            case '`': return 0xC0;
            case '~': shift = true; return 0xC0;
            default: return 0;
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

    private void ClearKeyboard_Clicked(object? sender, EventArgs e)
    {
        _suppressKeyboardSync = true;
        KeyboardEditor.Text = "";
        _keyboardPrev = "";
        _suppressKeyboardSync = false;
        KeyboardEditor.Focus();
    }

    private async void OpenSettings_Clicked(object? sender, EventArgs e)
        => await Navigation.PushAsync(new MainPage { Title = "设置" });
}
