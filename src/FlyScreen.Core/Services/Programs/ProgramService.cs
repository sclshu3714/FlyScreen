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
    }
}
