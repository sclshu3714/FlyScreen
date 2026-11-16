using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Network;
using FlyScreen.ToolSdk;

namespace FlyScreen.Services.Tools.Hosting;

public sealed class ToolHostServices : ICoreToolHostServices
{
    private readonly Dictionary<Type, object> _services = new();

    public ToolHostServices(
        ConnectionManager connections,
        AppSettings settings,
        ToolCatalog catalog,
        ToolWorkspace workspace,
        ToolInstallProgress installProgress)
    {
        Connections = connections;
        Settings = settings;
        Catalog = catalog;
        Workspace = workspace;
        InstallProgress = installProgress;
        _services[typeof(ConnectionManager)] = connections;
        _services[typeof(AppSettings)] = settings;
        _services[typeof(ToolCatalog)] = catalog;
        _services[typeof(ToolWorkspace)] = workspace;
        _services[typeof(IToolWorkspace)] = workspace;
        _services[typeof(ToolInstallProgress)] = installProgress;
    }

    public ConnectionManager Connections { get; }
    public AppSettings Settings { get; }
    public ToolCatalog Catalog { get; }
    public IToolWorkspace Workspace { get; }
    public ToolInstallProgress InstallProgress { get; }
    public IToolUiHost? Ui { get; set; }

    public T? GetService<T>() where T : class
    {
        if (typeof(T) == typeof(IToolUiHost))
            return Ui as T;
        return _services.TryGetValue(typeof(T), out var s) ? s as T : null;
    }

    public void RegisterService<T>(T instance) where T : class
        => _services[typeof(T)] = instance;

    public void UnregisterService<T>() where T : class
        => _services.Remove(typeof(T));

    public string GetToolDataDirectory(string toolId)
        => FlyScreenDataPaths.GetToolDataDirectory(toolId);
}
