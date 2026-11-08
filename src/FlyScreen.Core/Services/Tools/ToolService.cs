using FlyScreen.Models;
using FlyScreen.Services.Network;
using FlyScreen.Services.Tools.ColorPicker;
using FlyScreen.Services.Tools.SharedDevices;

namespace FlyScreen.Services.Tools;

/// <summary>各端共享的工具管理入口。</summary>
public sealed class ToolService
{
    public static ToolService Instance { get; } = new();

    public ToolCatalog Catalog { get; }
    public ToolInstaller Installer { get; }
    public ToolInstallProgress InstallProgress { get; }
    public ToolWorkspace Workspace { get; } = new();

    private readonly object _gate = new();
    private SharedDevicesService? _sharedDevices;
    private ColorPickerService? _colorPicker;

    private ToolService()
    {
        Catalog = new ToolCatalog();
        InstallProgress = new ToolInstallProgress();
        Installer = new ToolInstaller(Catalog, InstallProgress);
    }

    public SharedDevicesService? SharedDevices
    {
        get
        {
            lock (_gate) return _sharedDevices;
        }
    }

    public ColorPickerService ColorPicker
    {
        get
        {
            lock (_gate)
            {
                _colorPicker ??= new ColorPickerService();
                return _colorPicker;
            }
        }
    }

    /// <summary>各 AppHost 在持有 ConnectionManager / AppSettings 后调用一次。</summary>
    public SharedDevicesService AttachSharedDevices(ConnectionManager connections, AppSettings settings)
    {
        lock (_gate)
        {
            if (_sharedDevices != null)
                return _sharedDevices;
            _sharedDevices = new SharedDevicesService(connections, settings, Catalog);
            // 安装状态 → Caps；已连接对端无需重连即可看到 Tool:* 
            Catalog.CapsChanged += () =>
            {
                _ = connections.BroadcastLocalPeerInfoAsync();
            };
            Catalog.RefreshExtraCaps();
            _ = connections.BroadcastLocalPeerInfoAsync();
            return _sharedDevices;
        }
    }

    public void AttachColorSampler(IScreenColorSampler sampler)
    {
        ColorPicker.Sampler = sampler;
    }

    public ToolInstallResult Install(string toolId)
        => Installer.InstallBundled(toolId);

    public Task<ToolInstallResult> InstallAsync(string toolId, CancellationToken ct = default)
        => Installer.InstallBundledAsync(toolId, ct);

    public async Task<ToolInstallResult> UninstallAsync(string toolId)
    {
        if (string.Equals(toolId, ToolIds.SharedDevices, StringComparison.OrdinalIgnoreCase))
        {
            var sd = SharedDevices;
            if (sd != null && sd.IsRunning)
                await sd.StopAsync("uninstall").ConfigureAwait(false);
        }

        if (string.Equals(toolId, ToolIds.ColorPicker, StringComparison.OrdinalIgnoreCase))
        {
            ColorPicker.Stop();
            Workspace.Close(ToolIds.ColorPicker);
        }

        return Installer.Uninstall(toolId);
    }

    public void OpenColorPickerWorkspace()
    {
        var cp = ColorPicker;
        if (!cp.IsRunning)
            cp.Start();
        var title = Helpers.Localization.Loc.T("Tool.ColorPicker.Name");
        Workspace.OpenOrFocus(ToolIds.ColorPicker, title);
    }

    public void StopColorPickerWorkspace()
    {
        ColorPicker.CancelPicking();
        ColorPicker.Stop();
        Workspace.Close(ToolIds.ColorPicker);
    }
}
