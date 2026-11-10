using FlyScreen.Models;
using FlyScreen.Services.Network;
using FlyScreen.ToolSdk;

namespace FlyScreen.Services.Tools.Hosting;

/// <summary>Core 侧宿主服务（扩展 SDK 契约，供进程内与加载器使用）。</summary>
public interface ICoreToolHostServices : IToolHostServices
{
    ConnectionManager Connections { get; }
    AppSettings Settings { get; }
    ToolCatalog Catalog { get; }
    ToolInstallProgress InstallProgress { get; }
}
