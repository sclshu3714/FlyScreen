using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;
using FlyScreen.Services;
using FlyScreen.Services.ScreenExtend;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.SharedDevices;

/// <summary>共享设备模块（可打包进 .flytool；亦可进程内注册过渡）。</summary>
/// <summary>ALC 入口类型（manifest entryType）。</summary>
public sealed class Module : SharedDevicesToolModule
{
    public Module()
    {
    }

    public Module(SharedDevicesService service) : base(service)
    {
    }
}

/// <summary>共享设备工具模块。</summary>
public class SharedDevicesToolModule : IToolModule
{
    public const string UiStart = "start-dialog";
    public const string UiIncoming = "incoming";
    public const string MenuTest = "test";
    public const string MenuManage = "manage";
    public const string MenuStop = "stop";

    private SharedDevicesService? _service;
    private IToolHostServices? _host;
    private Action<SharedDevicesIncomingRequest>? _incomingHandler;

    static SharedDevicesToolModule()
    {
        SharedDevicesLocalization.EnsureRegistered();
        SharedDevicesIcons.EnsureRegistered();
    }

    /// <summary>ALC 无参入口：在 OnLoaded 时用宿主连接创建 Service。</summary>
    public SharedDevicesToolModule()
    {
    }

    public SharedDevicesToolModule(SharedDevicesService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    public string Id => ToolIds.SharedDevices;

    public bool IsRunning => _service?.IsRunning == true;

    public bool UsesWorkspace => false;

    public event Action? StateChanged
    {
        add
        {
            if (_service != null) _service.SessionChanged += value;
            _stateHandlers += value;
        }
        remove
        {
            if (_service != null) _service.SessionChanged -= value;
            _stateHandlers -= value;
        }
    }

    private Action? _stateHandlers;

    public SharedDevicesService? Service => _service;

    public Task OnLoadedAsync(IToolHostServices host, CancellationToken ct = default)
    {
        SharedDevicesLocalization.EnsureRegistered();
        SharedDevicesIcons.EnsureRegistered();
        _host = host;
        var connections = host.GetService<ConnectionManager>();
        var settings = host.GetService<AppSettings>();
        var catalog = host.GetService<ToolCatalog>();
        if (_service == null)
        {
            if (connections == null || settings == null || catalog == null)
                throw new InvalidOperationException("共享设备需要 ConnectionManager/AppSettings/ToolCatalog");
            _service = new SharedDevicesService(connections, settings, catalog);
            if (_stateHandlers != null)
                _service.SessionChanged += _stateHandlers;
        }

        host.RegisterService(_service);
        host.RegisterService<ISharedDevicesRuntime>(_service);

        if (catalog != null)
        {
            catalog.RegisterDynamicCapProvider(() =>
                _service?.BuildAdvertisedCaps() ?? []);
            catalog.RefreshExtraCaps();
        }

        var audio = host.GetService<IProjectionAudioEngine>();
        if (audio != null)
            _service.AttachAudioEngine(audio);

        _incomingHandler = req => { _ = HandleIncomingAsync(req); };
        _service.IncomingRequest += _incomingHandler;
        return Task.CompletedTask;
    }

    public Task OnUnloadedAsync(CancellationToken ct = default)
    {
        if (_service != null && _incomingHandler != null)
            _service.IncomingRequest -= _incomingHandler;
        _incomingHandler = null;
        var stop = _service?.IsRunning == true ? _service.StopAsync("unload") : Task.CompletedTask;
        try
        {
            _host?.UnregisterService<ISharedDevicesRuntime>();
            if (_service != null)
                _host?.UnregisterService<SharedDevicesService>();
        }
        catch { /* ignore */ }

        ToolIconRegistry.Unregister(SharedDevicesIcons.Instance.IconKey);
        _service = null;
        _host = null;
        // 内置清单卡片仍需 Name/Summary，卸载模块时不注销文案
        return stop;
    }

    public async Task<(bool Ok, string Message)> PrimaryAsync(
        ToolCardContext ctx, bool fromMenu, CancellationToken ct = default)
    {
        if (_service == null)
            return (false, "共享设备服务未就绪");

        if (ctx.Row.IsRunning)
        {
            if (!fromMenu)
                return (true, "");
            await _service.StopAsync().ConfigureAwait(true);
            return (true, "");
        }

        if (!ctx.Row.CanStart)
            return (true, "");

        return await StartViaUiAsync(ct).ConfigureAwait(true);
    }

    public Task StopAsync(CancellationToken ct = default)
        => _service?.IsRunning == true ? _service.StopAsync() : Task.CompletedTask;

    public IReadOnlyList<ToolMenuContribution> GetMenuContributions(ToolCardContext ctx)
    {
        if (!ctx.Row.IsInstalled) return [];
        var list = new List<ToolMenuContribution>();
        if (ctx.Row.IsRunning)
            list.Add(new ToolMenuContribution { ActionId = MenuStop, Label = Loc.T("Tool.Stop") });
        if (ctx.Row.CanConsume)
            list.Add(new ToolMenuContribution { ActionId = MenuTest, Label = Loc.T("Tool.Test") });
        if (ctx.Row.CanProvide)
            list.Add(new ToolMenuContribution { ActionId = MenuManage, Label = Loc.T("Tool.Manage") });
        return list;
    }

    public async Task InvokeMenuAsync(string actionId, CancellationToken ct = default)
    {
        if (string.Equals(actionId, MenuStop, StringComparison.OrdinalIgnoreCase))
        {
            await StopAsync(ct).ConfigureAwait(true);
            return;
        }

        if (_host?.Ui == null || _service == null) return;

        if (string.Equals(actionId, MenuTest, StringComparison.OrdinalIgnoreCase))
        {
            await _host.Ui.RequestAsync(Id, MenuTest, null, ct).ConfigureAwait(true);
            return;
        }

        if (string.Equals(actionId, MenuManage, StringComparison.OrdinalIgnoreCase))
            await _host.Ui.RequestAsync(Id, MenuManage, _service, ct).ConfigureAwait(true);
    }

    private async Task<(bool Ok, string Message)> StartViaUiAsync(CancellationToken ct)
    {
        var ui = _host?.Ui;
        if (ui == null || _service == null)
            return (false, "共享设备 UI 未注册");

        var result = await ui.RequestAsync(Id, UiStart, _service, ct).ConfigureAwait(true);
        if (result is not SharedDevicesStartUiResult start)
            return (true, "");

        return await _service.StartRequestAsync(start.Peer, start.Kinds).ConfigureAwait(true);
    }

    private async Task HandleIncomingAsync(SharedDevicesIncomingRequest req)
    {
        if (_service == null) return;
        var ui = _host?.Ui;
        if (ui == null)
        {
            await _service.RejectIncomingAsync(req).ConfigureAwait(true);
            return;
        }

        var result = await ui.RequestAsync(Id, UiIncoming, new SharedDevicesIncomingUiArgs(_service, req))
            .ConfigureAwait(true);
        if (result is SharedDevicesIncomingUiResult { Accepted: true } ok)
            await _service.AcceptIncomingAsync(req, remember: ok.Remember, deviceIds: ok.DeviceIds)
                .ConfigureAwait(true);
        else
            await _service.RejectIncomingAsync(req).ConfigureAwait(true);
    }
}

public sealed class SharedDevicesStartUiResult
{
    public required string Peer { get; init; }
    public required IReadOnlyList<string> Kinds { get; init; }
}

public sealed class SharedDevicesIncomingUiArgs
{
    public SharedDevicesIncomingUiArgs(SharedDevicesService service, SharedDevicesIncomingRequest request)
    {
        Service = service;
        Request = request;
    }

    public SharedDevicesService Service { get; }
    public SharedDevicesIncomingRequest Request { get; }
}

public sealed class SharedDevicesIncomingUiResult
{
    public bool Accepted { get; init; }
    public bool Remember { get; init; }
    public IReadOnlyDictionary<string, string>? DeviceIds { get; init; }
}
