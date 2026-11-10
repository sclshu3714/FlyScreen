using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;
using FlyScreen.Services.Tools.Hosting;
using FlyScreen.ToolSdk;

namespace FlyScreen.Services.Tools;

/// <summary>各端共享的工具管理入口（经 <see cref="Host"/> 调度；优先 ALC 加载已装包）。</summary>
public sealed class ToolService
{
    public static ToolService Instance { get; } = new();

    public ToolCatalog Catalog { get; }
    public ToolInstaller Installer { get; }
    public ToolInstallProgress InstallProgress { get; }
    public ToolWorkspace Workspace { get; } = new();
    public ToolHost Host { get; } = new();
    public ToolUiHost Ui { get; } = new();
    public ToolShortcutsStore Shortcuts { get; }
    public ToolRemoteIndexClient RemoteIndex { get; } = new();

    private readonly object _gate = new();
    private ToolHostServices? _hostServices;
    private IScreenColorSampler? _colorSampler;
    private IToolUiContext? _uiContext;

    private ToolService()
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        Catalog = new ToolCatalog();
        InstallProgress = new ToolInstallProgress();
        Installer = new ToolInstaller(Catalog, InstallProgress);
        Shortcuts = new ToolShortcutsStore();
    }

    /// <summary>已加载的颜色拾取器服务（实现位于工具包程序集）。</summary>
    public IColorPickerService? ColorPicker
    {
        get
        {
            lock (_gate)
                return _hostServices?.GetService<IColorPickerService>();
        }
    }

    /// <summary>从宿主服务容器取工具已注册的具体服务（如 SharedDevicesService）。</summary>
    public T? GetToolService<T>() where T : class
    {
        lock (_gate)
            return _hostServices?.GetService<T>();
    }

    /// <summary>挂接连接并加载已装/Bundled 工具模块（共享设备、颜色拾取等）。同步等待加载完成。</summary>
    public void AttachSharedDevices(ConnectionManager connections, AppSettings settings)
    {
        // 先种子旁路/AppBase Bundled，再扫清单与文案（对齐 Maui BundledFlyToolsSeeder）
        ToolBundledPackSeeder.TrySeedFromAppBase();
        Catalog.ReloadBuiltinFromBundled();
        ToolBundledCatalogAssets.Ensure();
        lock (_gate)
        {
            EnsureHostServicesUnlocked(connections, settings);
            Catalog.CapsChanged += () => { _ = connections.BroadcastLocalPeerInfoAsync(); };
            Catalog.RefreshExtraCaps();
            _ = connections.BroadcastLocalPeerInfoAsync();
        }

        ReloadInstalledModulesAsync().GetAwaiter().GetResult();
    }

    public void AttachHost(ConnectionManager connections, AppSettings settings)
    {
        lock (_gate)
            EnsureHostServicesUnlocked(connections, settings);
        ReloadInstalledModulesAsync().GetAwaiter().GetResult();
    }

    public void AttachColorSampler(IScreenColorSampler sampler)
    {
        lock (_gate)
        {
            _colorSampler = sampler;
            _hostServices?.RegisterService(sampler);
            var cp = _hostServices?.GetService<IColorPickerService>();
            if (cp != null)
                cp.Sampler = sampler;
        }
    }

    public void BindUi(IToolUiContext? context = null)
    {
        lock (_gate)
        {
            if (context != null)
                _uiContext = context;
            if (_hostServices != null)
                _hostServices.Ui = Ui;
        }
    }

    public async Task ReloadInstalledModulesAsync(CancellationToken ct = default)
    {
        await Host.LoadAllInstalledAsync(Catalog, ct).ConfigureAwait(false);
        foreach (var m in Catalog.BuiltinManifests)
        {
            if (string.IsNullOrWhiteSpace(m.EntryAssembly) || string.IsNullOrWhiteSpace(m.EntryType))
                continue;
            if (Host.Get(m.Id) != null)
                continue;
            await TryEnsureBundledLoadedAsync(m.Id, ct).ConfigureAwait(false);
        }

        lock (_gate) ApplyColorSamplerUnlocked();
    }

    public ToolInstallResult Install(string toolId)
        => Installer.InstallBundled(toolId);

    public async Task<ToolInstallResult> InstallAsync(string toolId, CancellationToken ct = default)
    {
        // 重装前先卸 ALC / UI，避免 Installed 目录 DLL 仍被占用
        try { await Host.UnloadAsync(toolId, ct).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn($"安装前停止工具失败 {toolId}: {ex.Message}"); }

        var local = ToolPackageResolver.ResolveLocalPack(toolId);
        ToolInstallResult result;
        if (local != null)
        {
            result = await Installer.InstallFromFlyToolFileAsync(local.Path,
                local.Source == "bundled" ? ToolTrustLevel.Bundled : ToolTrustLevel.KnownRelease, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var remote = await RemoteIndex.FindAsync(toolId, ct).ConfigureAwait(false);
            if (remote != null)
            {
                var cached = await RemoteIndex.DownloadToCacheAsync(remote, ct).ConfigureAwait(false);
                if (cached == null)
                    return ToolInstallResult.Fail("下载工具包失败");
                result = await Installer.InstallFromFlyToolFileAsync(cached, ToolTrustLevel.KnownRelease, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                result = await Installer.InstallBundledAsync(toolId, ct).ConfigureAwait(false);
            }
        }

        if (result.Success)
        {
            var rec = Catalog.FindRecord(toolId);
            if (rec != null && !string.IsNullOrEmpty(rec.Path) && Directory.Exists(rec.Path))
                await Host.LoadInstalledAsync(toolId, rec.Path, ct).ConfigureAwait(false);
            lock (_gate) ApplyColorSamplerUnlocked();
            RefreshToolUiContributors();
        }

        return result;
    }

    public async Task<ToolInstallResult> InstallFromFileAsync(string flytoolPath, CancellationToken ct = default)
    {
        var result = await Installer.InstallFromFlyToolFileAsync(flytoolPath, ToolTrustLevel.Unknown, ct)
            .ConfigureAwait(false);
        if (result.Success)
        {
            var id = (await ToolPackageResolver.ReadManifestFromPackAsync(flytoolPath, ct).ConfigureAwait(false))?.Id;
            if (!string.IsNullOrEmpty(id))
            {
                var rec = Catalog.FindRecord(id);
                if (rec?.Path != null)
                    await Host.LoadInstalledAsync(id, rec.Path, ct).ConfigureAwait(false);
                lock (_gate) ApplyColorSamplerUnlocked();
                RefreshToolUiContributors();
            }
        }

        return result;
    }

    /// <summary>安装/ALC 加载后补扫 UI 贡献者（宿主已 BindUi 时生效）。</summary>
    public void RefreshToolUiContributors()
    {
        IToolUiContext? ctx;
        lock (_gate) ctx = _uiContext;
        if (ctx == null) return;
        ToolUiDiscovery.RegisterAll(Ui, ctx);
    }

    public async Task<ToolInstallResult> UninstallAsync(string toolId)
    {
        var iconKey = Catalog.FindManifest(toolId)?.Icon;

        try { await Host.UnloadAsync(toolId).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn($"停止工具失败 {toolId}: {ex.Message}"); }

        var mod = Host.Get(toolId);
        if (mod?.UsesWorkspace == true)
            Workspace.Close(toolId);

        Shortcuts.ClearTool(toolId);
        if (!string.IsNullOrWhiteSpace(iconKey))
            ToolIconRegistry.Unregister(iconKey);

        var result = Installer.Uninstall(toolId);
        if (result.Success)
            RefreshToolUiContributors();
        return result;
    }

    public async Task<(bool Ok, string Message)> PrimaryAsync(
        ToolCardRow row, bool fromMenu, CancellationToken ct = default)
    {
        var module = Host.Get(row.Id);
        if (module == null)
            return (false, "未知工具：" + row.Id);
        var ctx = new ToolCardContext { Row = row.ToCardInfo() };
        return await module.PrimaryAsync(ctx, fromMenu, ct).ConfigureAwait(true);
    }

    public Task InvokeMenuAsync(string toolId, string actionId, CancellationToken ct = default)
    {
        var module = Host.Get(toolId);
        return module == null
            ? Task.CompletedTask
            : module.InvokeMenuAsync(actionId, ct);
    }

    public IReadOnlyList<ToolMenuContribution> GetMenuContributions(ToolCardRow row)
        => Host.Get(row.Id)?.GetMenuContributions(new ToolCardContext { Row = row.ToCardInfo() }) ?? [];

    public object? CreateWorkspaceContent(string toolId)
        => Ui.CreateWorkspaceContent(toolId);

    /// <summary>
    /// 仅加载/修复<strong>已安装</strong>工具；用户卸载后保持未安装，启动时不得静默装回。
    /// </summary>
    private async Task TryEnsureBundledLoadedAsync(string toolId, CancellationToken ct)
    {
        // 宿主若仍带有同程序集（过渡），可进程内注册，但不得因此写安装记录
        if (TryRegisterFromLoadedAssembly(toolId))
            return;

        // 未安装：卡片仍来自 Bundled 清单，等用户点「安装」
        if (!Catalog.IsInstalled(toolId))
            return;

        var pack = ToolPackageResolver.ResolveLocalPack(toolId);
        var rec = Catalog.FindRecord(toolId);
        var bundledVer = pack == null
            ? null
            : (await ToolPackageResolver.ReadManifestFromPackAsync(pack.Path, ct).ConfigureAwait(false))
                ?.PackageVersion;
        var versionStale = !string.IsNullOrWhiteSpace(bundledVer)
                           && rec != null
                           && !string.Equals(rec.Version, bundledVer, StringComparison.OrdinalIgnoreCase);

        if (!versionStale && ToolInstaller.IsInstallComplete(rec) && rec?.Path != null)
        {
            await Host.LoadInstalledAsync(toolId, rec.Path, ct).ConfigureAwait(false);
            if (Host.Get(toolId) != null)
            {
                ToolIconRegistry.EnsureFromLoadedAssemblies();
                var iconKey = Catalog.FindManifest(toolId)?.Icon;
                // 已加载且有图标（或无需图标）→ 完成
                if (string.IsNullOrWhiteSpace(iconKey)
                    || ToolIconRegistry.TryReadBitmapBytes(iconKey) != null)
                    return;
                AppLog.Warn($"工具缺少图标资源，准备升级重装：{toolId}");
            }
        }

        // 已安装但残缺/版本落后/缺图标：仅此时用 Bundled 包覆盖重装
        if (pack == null)
        {
            AppLog.Warn($"已安装工具无法修复（无本地包）：{toolId}");
            return;
        }

        AppLog.Warn($"工具需重装（残缺/版本变更/缺图标）：{toolId}");
        try { await Host.UnloadAsync(toolId, ct).ConfigureAwait(false); } catch { /* ignore */ }
        Installer.Uninstall(toolId);

        var r = await Installer.InstallFromFlyToolFileAsync(pack.Path,
            pack.Source == "bundled" ? ToolTrustLevel.Bundled : ToolTrustLevel.KnownRelease, ct)
            .ConfigureAwait(false);
        if (!r.Success) return;
        var installed = Catalog.FindRecord(toolId);
        if (installed?.Path != null)
            await Host.LoadInstalledAsync(toolId, installed.Path, ct).ConfigureAwait(false);
        ToolIconRegistry.EnsureFromLoadedAssemblies();
    }

    private bool TryRegisterFromLoadedAssembly(string toolId)
    {
        if (Host.Get(toolId) != null) return true;
        // Bundled 卡片探测可能已加载工具 DLL；未安装时不得注册模块
        if (!Catalog.IsInstalled(toolId))
            return false;

        var manifest = Catalog.BuiltinManifests.FirstOrDefault(m =>
            string.Equals(m.Id, toolId, StringComparison.OrdinalIgnoreCase));
        var asmName = manifest?.EntryAssembly;
        var typeName = manifest?.EntryType;
        if (string.IsNullOrWhiteSpace(asmName) || string.IsNullOrWhiteSpace(typeName))
        {
            // 已安装包目录内的 manifest
            var rec = Catalog.FindRecord(toolId);
            if (rec?.Path != null)
            {
                var mfPath = Path.Combine(rec.Path, "manifest.json");
                if (File.Exists(mfPath))
                {
                    try
                    {
                        var pkg = System.Text.Json.JsonSerializer.Deserialize<ToolPackageManifest>(
                            File.ReadAllText(mfPath), ToolPackageJson.Options);
                        if (pkg != null)
                        {
                            var tm = pkg.ToToolManifest();
                            asmName = tm.EntryAssembly;
                            typeName = tm.EntryType;
                        }
                    }
                    catch { /* ignore */ }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(asmName) || string.IsNullOrWhiteSpace(typeName))
            return false;

        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase));
        if (asm == null) return false;

        var type = asm.GetType(typeName, throwOnError: false);
        if (type == null || !typeof(IToolModule).IsAssignableFrom(type))
            return false;

        try
        {
            if (Activator.CreateInstance(type) is not IToolModule module)
                return false;
            Host.Register(module);
            Loc.EnsureToolLocalizationsFromLoadedAssemblies();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"进程内注册工具失败 {toolId}: {ex.Message}");
            return false;
        }
    }

    private void EnsureHostServicesUnlocked(ConnectionManager connections, AppSettings settings)
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        _hostServices ??= new ToolHostServices(connections, settings, Catalog, Workspace, InstallProgress);
        _hostServices.Ui = Ui;
        if (_colorSampler != null)
            _hostServices.RegisterService(_colorSampler);
        _ = Host.AttachAndLoadAsync(_hostServices);
    }

    private void ApplyColorSamplerUnlocked()
    {
        if (_colorSampler == null || _hostServices == null) return;
        _hostServices.RegisterService(_colorSampler);
        var cp = _hostServices.GetService<IColorPickerService>();
        if (cp != null)
            cp.Sampler = _colorSampler;
    }
}

public static class ToolCardRowExtensions
{
    public static ToolCardInfo ToCardInfo(this ToolCardRow row)
        => new()
        {
            Id = row.Id,
            IsInstalled = row.IsInstalled,
            IsInstalling = row.IsInstalling,
            IsRunning = row.IsRunning,
            CanStart = row.CanStart,
            CanConsume = row.CanConsume,
            CanProvide = row.CanProvide,
            HasWorkspace = row.HasWorkspace
        };
}
