namespace FlyScreen.Services.Programs;

/// <summary>各端共享的程序管理入口。</summary>
public sealed class ProgramService
{
    public static ProgramService Instance { get; } = new();

    public ProgramCatalog Catalog { get; }
    public ProgramRunner Runner { get; }

    private ProgramService()
    {
        Catalog = new ProgramCatalog();
        Runner = new ProgramRunner(Catalog);
        foreach (var p in Catalog.Items)
            Runner.SyncStatus(p);
        Catalog.Changed += () =>
        {
            foreach (var p in Catalog.Items)
                Runner.SyncStatus(p);
        };

        // 正常退出走各端 Dispose；崩溃、Environment.Exit 等仍先停子程序。
        // 任务管理器直接结束进程不会执行到这里。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Runner.StopAllForShutdown();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.IsTerminating)
                Runner.StopAllForShutdown();
        };
    }
}
