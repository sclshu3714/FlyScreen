using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Services.Tools;

/// <summary>
/// 从未安装的 Bundled .flytool 提取卡片文案/图标字节（不写安装记录、不长期驻留工具 ALC）。
/// </summary>
public static class ToolBundledCatalogAssets
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> ProbedPacks = new(StringComparer.OrdinalIgnoreCase);

    public static void Ensure()
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        ToolBundledPackSeeder.TrySeedFromAppBase();
        foreach (var packPath in ToolPackageResolver.EnumerateAllLocalPacks())
            TryProbePack(packPath);
    }

    private static void TryProbePack(string packPath)
    {
        if (string.IsNullOrWhiteSpace(packPath) || !File.Exists(packPath))
            return;

        string probeKey;
        try { probeKey = Path.GetFullPath(packPath) + "|" + File.GetLastWriteTimeUtc(packPath).Ticks; }
        catch { return; }

        lock (Gate)
        {
            if (ProbedPacks.Contains(probeKey))
                return;
        }

        try
        {
            using var zip = ZipFile.OpenRead(packPath);
            var mfEntry = zip.GetEntry("manifest.json")
                          ?? zip.Entries.FirstOrDefault(e =>
                              e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
            if (mfEntry == null)
            {
                MarkProbed(probeKey);
                return;
            }

            ToolPackageManifest? pack;
            using (var mfs = mfEntry.Open())
                pack = JsonSerializer.Deserialize<ToolPackageManifest>(mfs, ToolPackageJson.Options);
            if (pack == null || string.IsNullOrWhiteSpace(pack.EntryAssembly))
            {
                MarkProbed(probeKey);
                return;
            }

            // 已安装且模块在跑：文案/图标由正式加载贡献
            if (ToolService.Instance.Catalog.IsInstalled(pack.Id)
                && ToolService.Instance.Host.Get(pack.Id) != null)
            {
                MarkProbed(probeKey);
                return;
            }

            var needLoc = !string.IsNullOrWhiteSpace(pack.NameKey)
                          && string.Equals(Loc.T(pack.NameKey), pack.NameKey, StringComparison.Ordinal);
            var needIcon = !string.IsNullOrWhiteSpace(pack.Icon)
                           && ToolIconRegistry.TryReadBitmapBytes(pack.Icon) == null;
            if (!needLoc && !needIcon)
            {
                MarkProbed(probeKey);
                return;
            }

            var dllName = pack.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? pack.EntryAssembly
                : pack.EntryAssembly + ".dll";
            var dllEntry = zip.GetEntry(dllName)
                           ?? zip.Entries.FirstOrDefault(e =>
                               e.FullName.EndsWith(dllName, StringComparison.OrdinalIgnoreCase));
            if (dllEntry == null)
            {
                MarkProbed(probeKey);
                return;
            }

            using var dllStream = dllEntry.Open();
            using var ms = new MemoryStream();
            dllStream.CopyTo(ms);
            ms.Position = 0;

            var alc = new CatalogLoadContext("catalog:" + pack.Id);
            try
            {
                var asm = alc.LoadFromStream(ms);
                RegisterCatalogAssetsFrom(asm, pack.Icon);
            }
            finally
            {
                try { alc.Unload(); } catch { /* ignore */ }
            }

            MarkProbed(probeKey);
            AppLog.Info($"已从 Bundled 提取卡片资源：{pack.Id}");
        }
        catch (Exception ex)
        {
            MarkProbed(probeKey);
            AppLog.Warn($"Bundled 卡片资源探测失败 {packPath}: {ex.Message}");
        }
    }

    private static void RegisterCatalogAssetsFrom(Assembly asm, string? iconKey)
    {
        foreach (var type in ToolAssemblyScan.GetLoadableTypes(asm))
        {
            if (type.IsAbstract)
                continue;

            try
            {
                if (typeof(IToolLocalization).IsAssignableFrom(type))
                {
                    IToolLocalization? loc = null;
                    var instProp = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (instProp?.GetValue(null) is IToolLocalization fromProp)
                        loc = fromProp;
                    else if (Activator.CreateInstance(type) is IToolLocalization created)
                        loc = created;
                    if (loc != null)
                        Loc.RegisterPlugin(loc);
                }
            }
            catch { /* ignore */ }

            try
            {
                if (typeof(IToolIconContribution).IsAssignableFrom(type))
                {
                    IToolIconContribution? icon = null;
                    var instProp = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (instProp?.GetValue(null) is IToolIconContribution fromProp)
                        icon = fromProp;
                    else if (Activator.CreateInstance(type) is IToolIconContribution created)
                        icon = created;
                    if (icon == null) continue;

                    // 读入位图缓存后立刻注销贡献，避免依赖残缺 ALC 程序集
                    ToolIconRegistry.Register(icon);
                    _ = ToolIconRegistry.TryReadBitmapBytes(icon.IconKey);
                    ToolIconRegistry.Unregister(icon.IconKey, clearBitmapCache: false);
                }
            }
            catch { /* ignore */ }
        }

        if (!string.IsNullOrWhiteSpace(iconKey))
            _ = ToolIconRegistry.TryReadBitmapBytes(iconKey);
    }

    private static void MarkProbed(string probeKey)
    {
        lock (Gate) ProbedPacks.Add(probeKey);
    }

    private sealed class CatalogLoadContext : AssemblyLoadContext
    {
        // 与 ToolHost 一致：移动端 Mono 不可用可回收 ALC
        public CatalogLoadContext(string name)
            : base(name, isCollectible: Hosting.ToolHost.SupportsCollectibleLoadContext) { }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var n = assemblyName.Name;
            if (string.IsNullOrEmpty(n)) return null;
            // 契约与宿主共享；其余依赖不解析（卡片只取 Loc/图标）
            if (n is "FlyScreen.ToolSdk" or "FlyScreen.Core" or "FlyScreen.Assets"
                || n.StartsWith("System.", StringComparison.Ordinal)
                || n is "netstandard" or "mscorlib")
                return null;
            return null;
        }
    }
}
