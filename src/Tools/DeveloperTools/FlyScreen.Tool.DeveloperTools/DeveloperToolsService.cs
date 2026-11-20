using FlyScreen.Tool.DeveloperTools.Scaffold;

namespace FlyScreen.Tool.DeveloperTools;

public interface IDeveloperToolsService
{
    bool IsRunning { get; }
    event Action? SessionChanged;
    ToolScaffoldRequest Draft { get; }
    ToolScaffoldResult? LastResult { get; }
    void Start();
    void Stop();
    ToolScaffoldResult Generate();
}

public sealed class DeveloperToolsService : IDeveloperToolsService
{
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public event Action? SessionChanged;

    public ToolScaffoldRequest Draft { get; } = CreateDefaultDraft();

    public ToolScaffoldResult? LastResult { get; private set; }

    private static ToolScaffoldRequest CreateDefaultDraft()
    {
        var root = ToolScaffoldGenerator.TryFindDefaultOutputRoot();
        return new ToolScaffoldRequest
        {
            SupportDesktop = true,
            SupportPhone = false,
            UiMode = ToolUiMode.Workspace,
            // 仅当默认就落在源码仓库 Tools 下才勾选自动注册
            RegisterInRepo = ToolScaffoldGenerator.IsRepoToolsDirectory(root),
            OutputRoot = root
        };
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        if (string.IsNullOrWhiteSpace(Draft.OutputRoot))
            Draft.OutputRoot = ToolScaffoldGenerator.TryFindDefaultOutputRoot();
        SessionChanged?.Invoke();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        SessionChanged?.Invoke();
    }

    public ToolScaffoldResult Generate()
    {
        var result = ToolScaffoldGenerator.Generate(Draft);
        LastResult = result;
        SessionChanged?.Invoke();
        return result;
    }
}
