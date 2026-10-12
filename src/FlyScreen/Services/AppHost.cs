using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;

namespace FlyScreen.Services;

public sealed class AppHost : IAsyncDisposable
{
    public static AppHost Instance { get; private set; } = null!;

    public AppSettings Settings { get; }
    public ConnectionManager Connections { get; }
    public InputRouter Input { get; }
    public FileTransferService Transfers { get; }
    public ClipboardMonitor Clipboard { get; }

    public event Action<string>? StatusMessage;

    private AppHost()
    {
        Settings = AppSettings.Load();
        Settings.EnsureLocalMachineSlot();
        Connections = new ConnectionManager(Settings);
        Input = new InputRouter(Settings, Connections);
        Transfers = new FileTransferService(Settings, Connections);
        Clipboard = new ClipboardMonitor(Settings, Transfers);

        Connections.StatusMessage += m => StatusMessage?.Invoke(m);
        Transfers.StatusMessage += m => StatusMessage?.Invoke(m);
        Connections.MessageReceived += OnPeerMessage;
    }

    public static AppHost EnsureCreated()
    {
        Instance ??= new AppHost();
        return Instance;
    }

    public async Task StartAsync()
    {
        await Connections.StartAsync().ConfigureAwait(false);
        Input.EasyMouseEnabled = Settings.EasyMouse;
        Input.Start();
    }

    public async Task RestartNetworkingAsync()
    {
        Settings.Save();
        await Connections.StartAsync().ConfigureAwait(false);
    }

    private void OnPeerMessage(string peer, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.MouseMove:
            {
                var p = MessageCodec.DecodeJson<MouseMovePayload>(msg.Payload);
                if (p == null) return;
                if (p.Relative) InputInjector.MoveRelative(p.Dx, p.Dy);
                else InputInjector.MoveAbsoluteNormalized(p.Nx, p.Ny);
                break;
            }
            case MessageType.MouseButton:
            {
                var p = MessageCodec.DecodeJson<MouseButtonPayload>(msg.Payload);
                if (p != null) InputInjector.MouseButton(p);
                break;
            }
            case MessageType.MouseWheel:
            {
                var p = MessageCodec.DecodeJson<MouseWheelPayload>(msg.Payload);
                if (p != null) InputInjector.MouseWheel(p);
                break;
            }
            case MessageType.KeyEvent:
            {
                var p = MessageCodec.DecodeJson<KeyEventPayload>(msg.Payload);
                if (p != null) InputInjector.Key(p);
                break;
            }
            case MessageType.SwitchFocus:
            {
                // 对端通知焦点切到本机：本地恢复控制
                var p = MessageCodec.DecodeJson<SwitchFocusPayload>(msg.Payload);
                if (p == null) return;
                if (string.Equals(p.TargetMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    Input.TakeLocalControl();
                    InputInjector.MoveAbsoluteNormalized(p.EntryX, p.EntryY);
                }
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Clipboard.Dispose();
        Input.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        Settings.Save();
    }
}
