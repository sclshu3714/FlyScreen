using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Services.Tools;

/// <summary>
/// 从未安装的 Bundled .flytool 中加载卡片文案/图标（不写安装记录、不 Register 模块）。
/// </summary>
public static class ToolBundledCatalogAssets
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> ProbedPacks = new(StringComparer.OrdinalIgnoreCase);
    // 保持 ALC 存活，避免图标贡献的程序集被回收
    private static readonly List<AssemblyLoadContext> KeepAlive = [];

    public static void Ensure()
    {
        FlyScreenDataPaths.EnsureToolsLayout();
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
            if (mfEntry == null) return;

            ToolPackageManifest? pack;
            using (var mfs = mfEntry.Open())
                pack = JsonSerializer.Deserialize<ToolPackageManifest>(mfs, ToolPackageJson.Options);
            if (pack == null || string.IsNullOrWhiteSpace(pack.EntryAssembly))
                return;

            // 已安装且运行中：模块加载会注册文案/图标，此处跳过
            if (ToolService.Instance.Catalog.IsInstalled(pack.Id)
                && ToolService.Instance.Host.Get(pack.Id) != null)
            {
                lock (Gate) ProbedPacks.Add(probeKey);
                return;
            }

            var needLoc = !string.IsNullOrWhiteSpace(pack.NameKey)
                          && string.Equals(Loc.T(pack.NameKey), pack.NameKey, StringComparison.Ordinal);
            var needIcon = !string.IsNullOrWhiteSpace(pack.Icon)
                           && ToolIconRegistry.TryReadBitmapBytes(pack.Icon) == null;
            if (!needLoc && !needIcon)
            {
                lock (Gate) ProbedPacks.Add(probeKey);
                return;
            }

            var dllName = pack.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? pack.EntryAssembly
                : pack.EntryAssembly + ".dll";
            var dllEntry = zip.GetEntry(dllName)
                           ?? zip.Entries.FirstOrDefault(e =>
                               e.FullName.EndsWith(dllName, StringComparison.OrdinalIgnoreCase));
            if (dllEntry == null) return;

            using var dllStream = dllEntry.Open();
            using var ms = new MemoryStream();
            dllStream.CopyTo(ms);
            ms.Position = 0;

            var alc = new CatalogLoadContext("catalog:" + pack.Id);
            var asm = alc.LoadFromStream(ms);
            Loc.EnsureToolLocalizationsFromLoadedAssemblies();
            ToolIconRegistry.EnsureFromLoadedAssemblies();
            if (!string.IsNullOrWhiteSpace(pack.Icon))
                _ = ToolIconRegistry.TryReadBitmapBytes(pack.Icon);

            lock (Gate)
            {
                KeepAlive.Add(alc);
                ProbedPacks.Add(probeKey);
            }

            AppLog.Info($"已从 Bundled 探测卡片资源：{pack.Id}");
            _ = asm;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Bundled 卡片资源探测失败 {packPath}: {ex.Message}");
        }
    }

    private sealed class CatalogLoadContext : AssemblyLoadContext
    {
        public CatalogLoadContext(string name) : base(name, isCollectible: true) { }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var n = assemblyName.Name;
            if (string.IsNullOrEmpty(n)) return null;
            // 契约与宿主共享，避免 IToolLocalization 类型分裂
            if (n is "FlyScreen.ToolSdk" or "FlyScreen.Core" or "FlyScreen.Assets"
                || n.StartsWith("System.", StringComparison.Ordinal)
                || n is "netstandard" or "mscorlib")
                return null;
            return null;
        }
    }
}
