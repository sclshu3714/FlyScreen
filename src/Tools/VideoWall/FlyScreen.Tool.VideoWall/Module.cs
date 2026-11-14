using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall;

/// <summary>视频墙工具模块（.flytool ALC 入口）。</summary>
public sealed class Module : IToolModule
{
    public const string MenuStop = "stop";
    public const string MenuOpen = "open";

    private readonly VideoWallService _service = new();
    private IToolHostServices? _host;

    static Module()
    {
        VideoWallLocalization.EnsureRegistered();
        VideoWallIcons.EnsureRegistered();
    }

    public string Id => ToolIds.VideoWall;

    public bool IsRunning => _service.IsRunning;

    public bool UsesWorkspace => true;

    public VideoWallService Service => _service;

    public event Action? StateChanged
    {
        add => _service.SessionChanged += value;
        remove => _service.SessionChanged -= value;
    }

    public Task OnLoadedAsync(IToolHostServices host, CancellationToken ct = default)
    {
        VideoWallLocalization.EnsureRegistered();
        VideoWallIcons.EnsureRegistered();
        _host = host;
        _service.AttachHost(host);
        host.RegisterService<IVideoWallService>(_service);
        host.RegisterService(_service);
        return _service.InitializeAsync(ct);
    }

    public Task OnUnloadedAsync(CancellationToken ct = default)
    {
        _service.Stop();
        _host?.Workspace.Close(Id);
        try
        {
            _host?.UnregisterService<IVideoWallService>();
            _host?.UnregisterService<VideoWallService>();
        }
        catch { /* ignore */ }

        ToolIconRegistry.Unregister(VideoWallIcons.Instance.IconKey);
        _host = null;
        return Task.CompletedTask;
    }

    public Task<(bool Ok, string Message)> PrimaryAsync(
        ToolCardContext ctx, bool fromMenu, CancellationToken ct = default)
    {
        if (_host is null)
            return Task.FromResult((false, "工具宿主未就绪"));

        if (!VideoWallIds.BackendWired)
            return Task.FromResult((false, Loc.T("Tool.VideoWall.NotWired")));

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

    public Task StopAsync(CancellationToken ct = default) => StopInternalAsync();

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
        if (_host is null) return;
        if (!_service.IsRunning)
            _service.Start();
        _host.Workspace.OpenOrFocus(Id, Loc.T("Tool.VideoWall.Name"));
    }

    private Task<(bool Ok, string Message)> StopInternalAsync()
    {
        _service.Stop();
        _host?.Workspace.Close(Id);
        return Task.FromResult((true, ""));
    }
}