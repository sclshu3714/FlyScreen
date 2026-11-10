using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Services.Tools.Hosting;

/// <summary>工具模块注册表：支持进程内 Register 与从安装目录 ALC 加载。</summary>
public sealed class ToolHost
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IToolModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedIds = new(StringComparer.OrdinalIgnoreCase);
    private IToolHostServices? _services;

    public event Action? ModulesChanged;

    public IToolHostServices? Services
    {
        get { lock (_gate) return _services; }
    }

    public void Register(IToolModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.Id))
            throw new ArgumentException("module.Id required", nameof(module));

        IToolHostServices? services;
        var needLoad = false;
        lock (_gate)
        {
            _modules[module.Id.Trim()] = module;
            services = _services;
            needLoad = services != null && !_loadedIds.Contains(module.Id);
        }

        RaiseChanged();

        if (needLoad && services != null)
            _ = LoadOneAsync(module, services);
    }

    public IToolModule? Get(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return null;
        lock (_gate)
            return _modules.TryGetValue(toolId.Trim(), out var m) ? m : null;
    }

    public IReadOnlyList<IToolModule> All
    {
        get
        {
            lock (_gate)
                return _modules.Values.ToList();
        }
    }

    public bool IsRunning(string toolId)
        => Get(toolId)?.IsRunning == true;

    public async Task AttachAndLoadAsync(IToolHostServices services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        List<IToolModule> pending;
        lock (_gate)
        {
            _services = services;
            pending = _modules.Values.Where(m => !_loadedIds.Contains(m.Id)).ToList();
            foreach (var m in pending)
                _loadedIds.Add(m.Id);
        }

        foreach (var m in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await m.OnLoadedAsync(services, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"工具模块加载失败 {m.Id}: {ex.Message}");
                lock (_gate) _loadedIds.Remove(m.Id);
            }
        }
    }

    /// <summary>从已安装目录按 manifest 的 entryAssembly/entryType 加载。</summary>
    public async Task<IToolModule?> LoadInstalledAsync(string toolId, string installDir, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installDir);
        toolId = toolId.Trim();
        var manifestPath = Path.Combine(installDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            AppLog.Warn($"工具安装目录缺少 manifest.json: {installDir}");
            return null;
        }

        await using var fs = File.OpenRead(manifestPath);
        var pack = await JsonSerializer.DeserializeAsync<ToolPackageManifest>(fs, ToolPackageJson.Options, ct)
            .ConfigureAwait(false);
        if (pack == null || string.IsNullOrWhiteSpace(pack.EntryAssembly) || string.IsNullOrWhiteSpace(pack.EntryType))
        {
            AppLog.Warn($"工具 manifest 缺少 entryAssembly/entryType: {toolId}");
            return null;
        }

        var asmPath = ResolveEntryAssemblyPath(installDir, pack.EntryAssembly);
        if (asmPath == null || !File.Exists(asmPath))
        {
            AppLog.Warn($"找不到工具入口程序集: {pack.EntryAssembly} @ {installDir}");
            return null;
        }

        // 已加载则先卸
        await UnloadAsync(toolId, ct).ConfigureAwait(false);

        var alc = new ToolLoadContext(toolId, asmPath);
        try
        {
            // 按字节流加载，避免 Windows 锁住 Installed 目录下的 DLL（否则卸载/重装会失败）
            var asm = alc.LoadFromFileBytes(asmPath);
            LoadSatelliteUiAssemblies(alc, installDir, pack.EntryAssembly);
            var type = asm.GetType(pack.EntryType, throwOnError: false)
                       ?? asm.GetTypes().FirstOrDefault(t =>
                           string.Equals(t.FullName, pack.EntryType, StringComparison.OrdinalIgnoreCase));
            if (type == null || !typeof(IToolModule).IsAssignableFrom(type))
            {
                alc.Unload();
                AppLog.Warn($"工具入口类型无效: {pack.EntryType}");
                return null;
            }

            var module = (IToolModule?)Activator.CreateInstance(type);
            if (module == null)
            {
                alc.Unload();
                return null;
            }

            Loc.EnsureToolLocalizationsFromLoadedAssemblies();
            ToolIconRegistry.EnsureFromLoadedAssemblies();

            lock (_gate)
            {
                _contexts[toolId] = alc;
                _modules[toolId] = module;
            }

            RaiseChanged();

            var services = Services;
            if (services != null)
            {
                lock (_gate) _loadedIds.Add(toolId);
                await module.OnLoadedAsync(services, ct).ConfigureAwait(false);
            }

            return module;
        }
        catch (Exception ex)
        {
            try { alc.Unload(); } catch { /* ignore */ }
            AppLog.Warn($"ALC 加载工具失败 {toolId}: {ex.Message}");
            return null;
        }
    }

    public async Task UnloadAsync(string toolId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return;
        toolId = toolId.Trim();

        IToolModule? module;
        ToolLoadContext? ctx;
        IToolHostServices? services;
        lock (_gate)
        {
            _modules.TryGetValue(toolId, out module);
            _contexts.TryGetValue(toolId, out ctx);
            services = _services;
            _modules.Remove(toolId);
            _contexts.Remove(toolId);
            _loadedIds.Remove(toolId);
        }

        try { services?.Ui?.UnregisterTool(toolId); }
        catch (Exception ex) { AppLog.Warn($"清除工具 UI 注册失败 {toolId}: {ex.Message}"); }

        if (module != null)
        {
            try { await module.OnUnloadedAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn($"工具卸载失败 {toolId}: {ex.Message}"); }
        }

        module = null;

        if (ctx != null)
        {
            try { ctx.Unload(); }
            catch (Exception ex) { AppLog.Warn($"ALC Unload 失败 {toolId}: {ex.Message}"); }
            ctx = null;
            // 促使可回收 ALC 尽快释放（流式加载后文件通常已不锁，仍利于内存回收）
            for (var i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        RaiseChanged();
    }

    public async Task UnloadAllAsync(CancellationToken ct = default)
    {
        List<string> ids;
        lock (_gate) ids = _modules.Keys.ToList();
        foreach (var id in ids)
            await UnloadAsync(id, ct).ConfigureAwait(false);
    }

    public async Task LoadAllInstalledAsync(ToolCatalog catalog, CancellationToken ct = default)
    {
        foreach (var rec in catalog.GetInstalledRecords())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rec.Path) || !Directory.Exists(rec.Path))
                continue;
            if (Get(rec.Id) != null) continue;
            await LoadInstalledAsync(rec.Id, rec.Path, ct).ConfigureAwait(false);
        }
    }

    private async Task LoadOneAsync(IToolModule module, IToolHostServices services)
    {
        lock (_gate)
        {
            if (!_loadedIds.Add(module.Id))
                return;
        }

        try
        {
            await module.OnLoadedAsync(services).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"工具模块加载失败 {module.Id}: {ex.Message}");
            lock (_gate) _loadedIds.Remove(module.Id);
        }
    }

    private static string? ResolveEntryAssemblyPath(string installDir, string entryAssembly)
    {
        var name = entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? entryAssembly
            : entryAssembly + ".dll";

        var direct = Path.Combine(installDir, name);
        if (File.Exists(direct)) return direct;

        // runtimes/<rid>/...
        var rid = ToolRuntimeId.Current;
        var underRid = Path.Combine(installDir, "runtimes", rid, name);
        if (File.Exists(underRid)) return underRid;

        foreach (var f in Directory.EnumerateFiles(installDir, name, SearchOption.AllDirectories))
            return f;
        return null;
    }

    /// <summary>同包内平台 UI 卫星程序集（Ui.Win / Ui.Desktop / Ui.Maui）。</summary>
    private static void LoadSatelliteUiAssemblies(ToolLoadContext alc, string installDir, string entryAssembly)
    {
        var logicName = entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? entryAssembly[..^4]
            : entryAssembly;
        if (string.IsNullOrWhiteSpace(logicName))
            return;

        foreach (var suffix in ResolveUiAssemblySuffixes())
        {
            var uiName = logicName + suffix + ".dll";
            var path = Path.Combine(installDir, uiName);
            if (!File.Exists(path))
                continue;
            try
            {
                alc.LoadFromFileBytes(path);
                AppLog.Info($"已加载工具 UI: {uiName}");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"加载工具 UI 失败 {uiName}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> ResolveUiAssemblySuffixes()
    {
        // 只加载当前宿主 UI 栈对应的卫星 DLL，避免 WPF 误载 Ui.Maui（工作区工厂被覆盖）
        var suffix = ToolUiHostKind.PreferredAssemblySuffix;
        if (!string.IsNullOrEmpty(suffix))
            yield return suffix;
    }

    private void RaiseChanged()
    {
        try { ModulesChanged?.Invoke(); } catch { /* ignore */ }
    }

    private sealed class ToolLoadContext : AssemblyLoadContext
    {
        private readonly string _pluginDir;
        private readonly AssemblyDependencyResolver _resolver;

        public ToolLoadContext(string name, string mainAssemblyPath)
            : base(name, isCollectible: true)
        {
            _pluginDir = Path.GetDirectoryName(mainAssemblyPath) ?? mainAssemblyPath;
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        public Assembly LoadFromFileBytes(string assemblyPath)
        {
            var dll = File.ReadAllBytes(assemblyPath);
            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (File.Exists(pdbPath))
            {
                var pdb = File.ReadAllBytes(pdbPath);
                return LoadFromStream(new MemoryStream(dll), new MemoryStream(pdb));
            }

            return LoadFromStream(new MemoryStream(dll));
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 与宿主共享契约 / Core / UI 框架，避免类型身份分裂
            if (ShouldShareWithDefaultContext(assemblyName.Name))
                return null;

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null)
                return LoadFromFileBytes(path);

            var fallback = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
            if (File.Exists(fallback))
                return LoadFromFileBytes(fallback);

            return null;
        }

        private static bool ShouldShareWithDefaultContext(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name is "FlyScreen.ToolSdk" or "FlyScreen.Core" or "FlyScreen.Assets")
                return true;
            if (name is "netstandard" or "mscorlib")
                return true;
            if (name.StartsWith("System.", StringComparison.Ordinal))
                return true;
            if (name is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "System.Xaml")
                return true;
            if (name.StartsWith("Avalonia", StringComparison.Ordinal))
                return true;
            if (name.StartsWith("Microsoft.Maui", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
                return true;
            if (name.StartsWith("Xamarin.", StringComparison.Ordinal)
                || name.StartsWith("AndroidX.", StringComparison.Ordinal)
                || name.StartsWith("Android.", StringComparison.Ordinal))
                return true;
            return false;
        }
    }
}
