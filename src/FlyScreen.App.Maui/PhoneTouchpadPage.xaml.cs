using FlyScreen.AppMaui.Services;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui;

public partial class PhoneTouchpadPage : ContentPage
{
    private readonly MauiAppHost _host;
    private Point? _last;
    private string? _activePeer;

    public PhoneTouchpadPage()
    {
        InitializeComponent();
        _host = MauiAppHost.EnsureCreated();
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPan;
        PadArea.GestureRecognizers.Add(pan);
        var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
        tap.Tapped += async (_, _) => await ClickAsync(0);
        PadArea.GestureRecognizers.Add(tap);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        _host.StatusMessage += msg => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = msg);
        await _host.StartAsync();
        PickPeer();
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

    private async void OnPan(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started)
            _last = new Point(e.TotalX, e.TotalY);
        else if (e.StatusType == GestureStatus.Running && _last is Point prev)
        {
            var dx = (int)Math.Round(e.TotalX - prev.X);
            var dy = (int)Math.Round(e.TotalY - prev.Y);
            _last = new Point(e.TotalX, e.TotalY);
            if (dx == 0 && dy == 0) return;
            PickPeer();
            if (string.IsNullOrEmpty(_activePeer)) return;
            await _host.Connections.SendToAsync(_activePeer, MessageType.MouseMove,
                MessageCodec.JsonBytes(new MouseMovePayload { Relative = true, Dx = dx, Dy = dy }));
        }
        else if (e.StatusType is GestureStatus.Completed or GestureStatus.Canceled)
            _last = null;
    }

    private async void Left_Clicked(object? sender, EventArgs e) => await ClickAsync(0);
    private async void Right_Clicked(object? sender, EventArgs e) => await ClickAsync(1);
    private async void Wheel_Clicked(object? sender, EventArgs e)
    {
        PickPeer();
        if (string.IsNullOrEmpty(_activePeer)) return;
        await _host.Connections.SendToAsync(_activePeer, MessageType.MouseWheel,
            MessageCodec.JsonBytes(new MouseWheelPayload { Delta = -120 }));
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

    private async void OpenSettings_Clicked(object? sender, EventArgs e)
        => await Navigation.PushAsync(new MainPage { Title = "设置" });
}

