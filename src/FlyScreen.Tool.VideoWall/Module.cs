using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall;

/// <summary>
/// 视频墙工具模块（.flytool ALC 入口）。
/// 骨架阶段只完成模块契约与工作区入口；播放后端、源目录、场景读写均未接线。
/// 实现顺序见 <c>src/FlyScreen.Tool.VideoWall/README.md</c>。
/// </summary>
public sealed class Module : IToolModule
{
    public const string MenuStop = "stop";
    public const string MenuOpen = "open";

    private IToolHostServices? _host;
    private bool _isRunning;

    public string Id => VideoWallIds.ToolId;

    public bool IsRunning => _isRunning;

    /// <summary>主界面嵌入工具页工作区（非独立弹窗）。</summary>
    public bool UsesWorkspace => true;

    public event Action? StateChanged;

    public Task OnLoadedAsync(IToolHostServices host, CancellationToken ct = default)
    {
        _host = host;

        // TODO(接线 1)：注册 IVideoWallService 到宿主，供 UI 卫星经 GetService<IVideoWallService>() 取服务：
        //   host.RegisterService<IVideoWallService>(service);
        // TODO(接线 2)：本地化按 ColorPickerLocalization 的模式在宿主 Loc 注册：
        //   Loc.RegisterPlugin(VideoWallLocalization.Instance);
        // TODO(接线 3)：图标键注册见 ColorPickerIcons / ToolIconRegistry 的用法。
        return Task.CompletedTask;
    }

    public Task OnUnloadedAsync(CancellationToken ct = default)
    {
        _isRunning = false;
        _host?.Ui?.UnregisterTool(Id);
        _host?.Workspace.Close(Id);
        _host = null;
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task<(bool Ok, string Message)> PrimaryAsync(
        ToolCardContext ctx, bool fromMenu, CancellationToken ct = default)
    {
        if (_host == null)
            return Task.FromResult((false, "工具宿主未就绪"));

        if (!VideoWallIds.BackendWired)
            return Task.FromResult((false, "视频墙骨架：播放后端尚未接线"));

        if (ctx.Row.IsRunning && fromMenu)
            return StopInternalAsync();

        if (!_isRunning)
        {
            _isRunning = true;
            StateChanged?.Invoke();
        }

        _host.Workspace.OpenOrFocus(Id, "视频墙");
        return Task.FromResult((true, ""));
    }

    public Task StopAsync(CancellationToken ct = default) => StopInternalAsync();

    public IReadOnlyList<ToolMenuContribution> GetMenuContributions(ToolCardContext ctx)
    {
        if (!ctx.Row.IsInstalled) return [];

        var list = new List<ToolMenuContribution>();
        if (_isRunning)
            list.Add(new ToolMenuContribution { ActionId = MenuStop, Label = "停止" });
        return list;
    }

    public Task InvokeMenuAsync(string actionId, CancellationToken ct = default)
        => string.Equals(actionId, MenuStop, StringComparison.OrdinalIgnoreCase)
            ? StopInternalAsync()
            : Task.CompletedTask;

    private Task<(bool Ok, string Message)> StopInternalAsync()
    {
        _isRunning = false;
        _host?.Workspace.Close(Id);
        StateChanged?.Invoke();
        return Task.FromResult((true, ""));
    }
}
