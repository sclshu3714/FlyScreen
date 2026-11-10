using System.Text.Json;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Tools;

/// <summary>内置工具清单 + 本机安装状态（不进 BehaviorSync）。</summary>
public sealed class ToolCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly string _statePath;
    private ToolInstallStateFile _state = new();
    private IReadOnlyList<ToolManifest> _builtin;

    public event Action? Changed;
    /// <summary>本机工具 Caps 聚合变化（安装/卸载后）；宿主应广播 PeerInfo。</summary>
    public event Action? CapsChanged;

    public ToolCatalog(string? statePath = null)
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        _statePath = statePath ?? FlyScreenDataPaths.ToolsInstallStateFile;
        _builtin = BuildBuiltin();
        Reload();
        RefreshExtraCaps();
    }

    /// <summary>内置工具清单：优先从 <c>Tools/Bundled/*.flytool</c> 读取，便于新增工具不改宿主源码。</summary>
    public IReadOnlyList<ToolManifest> BuiltinManifests
    {
        get { lock (_gate) return _builtin; }
    }

    /// <summary>重新扫描 Bundled 目录（宿主拷贝完 flytool 后可调用）。</summary>
    public void ReloadBuiltinFromBundled()
    {
        var next = BuildBuiltin();
        lock (_gate) _builtin = next;
        RefreshExtraCaps();
        RaiseChanged();
    }

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_statePath))
                {
                    var json = File.ReadAllText(_statePath);
                    _state = JsonSerializer.Deserialize<ToolInstallStateFile>(json, JsonOptions) ?? new();
                }
                else
                {
                    _state = new ToolInstallStateFile();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("读取工具安装状态失败: " + ex.Message);
                _state = new ToolInstallStateFile();
            }
        }

        RefreshExtraCaps();
        RaiseChanged();
    }

    public IReadOnlyList<ToolCardVm> GetCardsForLocalDevice()
    {
        var platform = LocalPeerIdentity.Platform;
        var deviceClass = LocalPeerIdentity.DeviceClass;
        var list = new List<ToolCardVm>();
        foreach (var m in BuiltinManifests)
        {
            if (!IsApplicable(m, platform, deviceClass))
                continue;
            var rec = FindRecord(m.Id);
            var features = ResolveFeatures(m, deviceClass);
            list.Add(new ToolCardVm
            {
                Manifest = m,
                Status = rec == null ? ToolInstallStatus.NotInstalled : ToolInstallStatus.Installed,
                InstalledVersion = rec?.Version,
                CanConsume = features.Contains("consume", StringComparer.OrdinalIgnoreCase),
                CanProvide = features.Contains("provide", StringComparer.OrdinalIgnoreCase)
            });
        }

        return list;
    }

    public bool IsInstalled(string toolId)
        => FindRecord(toolId) != null;

    public ToolInstallRecord? FindRecord(string toolId)
    {
        lock (_gate)
        {
            return _state.Tools.FirstOrDefault(t =>
                string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<ToolInstallRecord> GetInstalledRecords()
    {
        lock (_gate)
            return _state.Tools.ToList();
    }

    /// <summary>合并内置清单与已发现的本地/远程包清单（同 Id 保留已有，补充新包）。</summary>
    public void MergeExternalManifests(IEnumerable<ToolManifest> extras)
    {
        // 本期 Catalog 仍以 Builtin 为主；远程条目经 ToolService.DiscoverManifests 合成卡片
        _ = extras;
    }

    public ToolManifest? FindManifest(string toolId)
        => BuiltinManifests.FirstOrDefault(m =>
            string.Equals(m.Id, toolId, StringComparison.OrdinalIgnoreCase));

    public void UpsertInstall(ToolInstallRecord record)
    {
        lock (_gate)
        {
            _state.Tools.RemoveAll(t =>
                string.Equals(t.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            _state.Tools.Add(record);
            SaveUnlocked();
        }

        RefreshExtraCaps();
        RaiseChanged();
    }

    public void RemoveInstall(string toolId)
    {
        lock (_gate)
        {
            _state.Tools.RemoveAll(t =>
                string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
            SaveUnlocked();
        }

        RefreshExtraCaps();
        RaiseChanged();
    }

    public void RefreshExtraCaps()
    {
        var caps = new List<string>();
        lock (_gate)
        {
            foreach (var rec in _state.Tools)
            {
                var m = FindManifest(rec.Id);
                if (m == null) continue;
                foreach (var c in m.CapNames)
                {
                    if (!string.IsNullOrWhiteSpace(c))
                        caps.Add(c.Trim());
                }
            }
        }

        LocalPeerIdentity.SetExtraCaps(caps);
        try { CapsChanged?.Invoke(); } catch { /* ignore */ }
    }

    public static string DisplayName(ToolManifest m)
        => Loc.T(m.NameKey);

    public static string DisplaySummary(ToolManifest m)
        => Loc.T(m.SummaryKey);

    private void SaveUnlocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn("保存工具安装状态失败: " + ex.Message);
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* ignore */ }
    }

    private static bool IsApplicable(ToolManifest m, string platform, string deviceClass)
    {
        if (m.DeviceClasses.Length > 0
            && !m.DeviceClasses.Any(d => d.Equals(deviceClass, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (m.Platforms.Length > 0
            && !m.Platforms.Any(p => p.Equals(platform, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    private static HashSet<string> ResolveFeatures(ToolManifest m, string deviceClass)
    {
        if (m.FeaturesByDeviceClass != null
            && m.FeaturesByDeviceClass.TryGetValue(deviceClass, out var feats)
            && feats != null)
            return new HashSet<string>(feats, StringComparer.OrdinalIgnoreCase);

        // 缺省：电脑可申请可提供；手机/平板第一期以提供为主
        if (deviceClass.Equals(PeerDeviceClass.Desktop, StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<string>(
                ["speaker", "microphone", "camera", "provide", "consume"],
                StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            ["speaker", "microphone", "camera", "provide"],
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ToolManifest> BuildBuiltin()
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        var fromBundled = LoadFromBundledDirectory();
        if (fromBundled.Count > 0)
            return fromBundled;
        return BuildFallbackBuiltin();
    }

    private static IReadOnlyList<ToolManifest> LoadFromBundledDirectory()
    {
        var dir = FlyScreenDataPaths.ToolsBundledDirectory;
        if (!Directory.Exists(dir))
            return [];

        var byId = new Dictionary<string, ToolManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var packPath in Directory.EnumerateFiles(dir, "*.flytool", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var pack = ToolPackageResolver.ReadManifestFromPackAsync(packPath).GetAwaiter().GetResult();
                if (pack == null || string.IsNullOrWhiteSpace(pack.Id))
                    continue;
                byId[pack.Id.Trim()] = pack.ToToolManifest();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读取 Bundled 清单失败 {Path.GetFileName(packPath)}: {ex.Message}");
            }
        }

        return byId.Values.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Bundled 尚未就位时的保底（开发/单测）；正式运行以目录扫描为准。</summary>
    private static IReadOnlyList<ToolManifest> BuildFallbackBuiltin()
        =>
        [
            new ToolManifest
            {
                Id = ToolIds.SharedDevices,
                Kind = "enhancement",
                CrossScreen = true,
                Group = "shared-devices",
                NameKey = "Tool.SharedDevices.Name",
                SummaryKey = "Tool.SharedDevices.Summary",
                SchemaVersion = 1,
                DeviceClasses = ["Desktop", "Phone", "Tablet"],
                Platforms = ["Windows", "Android", "macOS", "Linux"],
                FeaturesByDeviceClass = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Desktop"] = ["speaker", "microphone", "camera", "provide", "consume"],
                    ["Phone"] = ["speaker", "microphone", "camera", "provide"],
                    ["Tablet"] = ["speaker", "microphone", "camera", "provide"]
                },
                Roles = ["request", "authorize"],
                CapNames = [PeerCaps.ToolSharedDevices],
                TitleBar = false,
                HasWorkspace = false,
                Icon = "tool-shared-devices",
                Permissions = ["camera", "microphone", "speaker", "virtualAudio", "peerNetwork"],
                PackageVersion = "1.0.0",
                EntryAssembly = "FlyScreen.Tool.SharedDevices",
                EntryType = "FlyScreen.Tool.SharedDevices.Module"
            },
            new ToolManifest
            {
                Id = ToolIds.ColorPicker,
                Kind = "enhancement",
                CrossScreen = false,
                Group = "utilities",
                NameKey = "Tool.ColorPicker.Name",
                SummaryKey = "Tool.ColorPicker.Summary",
                SchemaVersion = 1,
                DeviceClasses = ["Desktop"],
                Platforms = ["Windows", "macOS", "Linux"],
                FeaturesByDeviceClass = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Desktop"] = ["local-pick"]
                },
                Roles = ["local"],
                CapNames = [],
                TitleBar = false,
                HasWorkspace = true,
                Icon = "tool-color-picker",
                Permissions = ["screenSample"],
                PackageVersion = "1.0.0",
                EntryAssembly = "FlyScreen.Tool.ColorPicker",
                EntryType = "FlyScreen.Tool.ColorPicker.Module"
            }
        ];
}
