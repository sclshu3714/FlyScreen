using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy;

/// <summary>端口代理工具模块（.flytool ALC 入口；Windows 专版）。</summary>
public sealed class Module : IToolModule
{
    public const string MenuStop = "stop";

    private readonly PortProxyService _service = new();
    private IToolHostServices? _host;

    static Module()
    {
        PortProxyLocalization.EnsureRegistered();
        PortProxyIcons.EnsureRegistered();
    }

    public string Id => ToolIds.PortProxy;

    public bool IsRunning => _service.IsRunning;

    public bool UsesWorkspace => true;

    public PortProxyService Service => _service;

    public event Action? StateChanged
    {
        add => _service.SessionChanged += value;
        remove => _service.SessionChanged -= value;
    }

    public Task OnLoadedAsync(IToolHostServices host, CancellationToken ct = default)
    {
        PortProxyLocalization.EnsureRegistered();
        PortProxyIcons.EnsureRegistered();
        _host = host;
        host.RegisterService<IPortProxyService>(_service);
        host.RegisterService(_service);
        return Task.CompletedTask;
    }

    public Task OnUnloadedAsync(CancellationToken ct = default)
    {
        _service.Stop();
        _host?.Workspace.Close(Id);
        try
        {
            _host?.UnregisterService<IPortProxyService>();
            _host?.UnregisterService<PortProxyService>();
        }
        catch { /* ignore */ }

        ToolIconRegistry.Unregister(PortProxyIcons.Instance.IconKey);
        _host = null;
        return Task.CompletedTask;
    }

    public Task<(bool Ok, string Message)> PrimaryAsync(
        ToolCardContext ctx, bool fromMenu, CancellationToken ct = default)
    {
        if (_host == null)
            return Task.FromResult((false, "工具宿主未就绪"));

        if (ctx.Row.IsRunning)
        {
            if (!fromMenu)
            {
                OpenWorkspace();
                return Task.FromResult((true, ""));
            }

            return StopInternalAsync();
        }

        if (!ctx.Row.CanStart)
            return Task.FromResult((false, ""));

        if (!_service.IsRunning)
            _service.Start();
        OpenWorkspace();
        return Task.FromResult((true, ""));
    }

    public Task StopAsync(CancellationToken ct = default)
        => StopInternalAsync();

    public IReadOnlyList<ToolMenuContribution> GetMenuContributions(ToolCardContext ctx)
    {
        if (!ctx.Row.IsInstalled) return [];
        var list = new List<ToolMenuContribution>();
        if (ctx.Row.IsRunning)
            list.Add(new ToolMenuContribution { ActionId = MenuStop, Label = Loc.T("Tool.Stop") });
        return list;
    }

    public Task InvokeMenuAsync(string actionId, CancellationToken ct = default)
    {
        if (string.Equals(actionId, MenuStop, StringComparison.OrdinalIgnoreCase))
            return StopInternalAsync();
        return Task.CompletedTask;
    }

    public void OpenWorkspace()
    {
        if (_host == null) return;
        if (!_service.IsRunning)
            _service.Start();
        _host.Workspace.OpenOrFocus(Id, Loc.T("Tool.PortProxy.Name"));
    }

    private Task<(bool Ok, string Message)> StopInternalAsync()
    {
        _service.Stop();
        _host?.Workspace.Close(Id);
        return Task.FromResult((true, ""));
    }
}
