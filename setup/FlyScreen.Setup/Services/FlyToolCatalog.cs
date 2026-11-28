using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.Setup.Services;

/// <summary>
/// 发布工具可选的插件清单：扫描 <c>src/Tools/**/manifest.json</c>（与 Pack-FlyTools 目录约定一致）。
/// </summary>
public static class FlyToolCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly object Gate = new();
    private static string? _cachedRoot;
    private static IReadOnlyList<FlyToolDescriptor>? _cached;

    /// <summary>最近一次 Discover 结果；尚未扫描时按仓库根自动扫描。</summary>
    public static IReadOnlyList<FlyToolDescriptor> All
    {
        get
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;
                return Discover(RepoPaths.FindRepoRoot());
            }
        }
    }

    /// <summary>扫描仓库插件目录；结果缓存到再次 Refresh。</summary>
    public static IReadOnlyList<FlyToolDescriptor> Discover(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        repoRoot = Path.GetFullPath(repoRoot);
        var toolsRoot = Path.Combine(repoRoot, "src", "Tools");
        var list = new List<FlyToolDescriptor>();
        if (!Directory.Exists(toolsRoot))
        {
            lock (Gate)
            {
                _cachedRoot = repoRoot;
                _cached = list;
            }
            return list;
        }

        foreach (var groupDir in Directory.EnumerateDirectories(toolsRoot))
        {
            foreach (var coreDir in Directory.EnumerateDirectories(groupDir, "FlyScreen.Tool.*"))
            {
                var coreName = Path.GetFileName(coreDir);
                if (coreName.Contains(".Ui.", StringComparison.OrdinalIgnoreCase))
                    continue;

                var manifestPath = Path.Combine(coreDir, "manifest.json");
                var csproj = Path.Combine(coreDir, coreName + ".csproj");
                if (!File.Exists(manifestPath) || !File.Exists(csproj))
                    continue;

                FlyToolManifestDto? mf;
                try
                {
                    mf = JsonSerializer.Deserialize<FlyToolManifestDto>(
                        File.ReadAllText(manifestPath), JsonOptions);
                }
                catch
                {
                    continue;
                }

                if (mf == null || string.IsNullOrWhiteSpace(mf.Id))
                    continue;

                var platforms = (mf.Platforms ?? [])
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (platforms.Length == 0)
                    platforms = ["Windows"];

                var display = ResolveDisplayName(coreDir, mf.NameKey, mf.Id);
                list.Add(new FlyToolDescriptor(mf.Id.Trim(), display, platforms));
            }
        }

        list.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        lock (Gate)
        {
            _cachedRoot = repoRoot;
            _cached = list;
        }
        return list;
    }

    /// <summary>强制重新扫描（发布工具启动或刷新时调用）。</summary>
    public static IReadOnlyList<FlyToolDescriptor> Refresh(string repoRoot)
    {
        lock (Gate)
        {
            _cached = null;
            _cachedRoot = null;
        }
        return Discover(repoRoot);
    }

    /// <summary>打包目标 Id → 清单 platforms 值；null 表示不限（插件组包目标）。</summary>
    public static string? ResolvePlatform(string? targetId) => targetId switch
    {
        "win" or "maui-win" => "Windows",
        "maui-android" => "Android",
        "linux" => "Linux",
        "mac" or "maui-mac" => "macOS",
        "flytools" => null,
        _ => null
    };

    public static bool SupportsPlatform(FlyToolDescriptor tool, string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return true;
        return tool.Platforms.Any(p => p.Equals(platform, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveDisplayName(string coreDir, string? nameKey, string id)
    {
        if (!string.IsNullOrWhiteSpace(nameKey))
        {
            var fromLoc = TryReadNameFromLocalization(coreDir, nameKey.Trim());
            if (!string.IsNullOrWhiteSpace(fromLoc))
                return fromLoc!;
        }

        // folder: FileVault → 文件保险箱 不可靠；退回 id
        return id;
    }

    /// <summary>
    /// 从工具目录 *Localization*.cs 中取 nameKey 的中文（优先）或英文赋值。
    /// </summary>
    private static string? TryReadNameFromLocalization(string coreDir, string nameKey)
    {
        string? zh = null;
        string? en = null;
        var keyPat = Regex.Escape(nameKey);
        var assign = new Regex(
            @"\[\s*""" + keyPat + @"""\s*\]\s*=\s*""([^""]*)""",
            RegexOptions.CultureInvariant);

        foreach (var file in Directory.EnumerateFiles(coreDir, "*Localization*.cs", SearchOption.TopDirectoryOnly))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            foreach (Match m in assign.Matches(text))
            {
                var val = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(val)) continue;
                if (ContainsCjk(val))
                {
                    zh ??= val;
                    break;
                }
                en ??= val;
            }
            if (zh != null) break;
        }

        return zh ?? en;
    }

    private static bool ContainsCjk(string s)
    {
        foreach (var ch in s)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF)
                return true;
        }
        return false;
    }

    private sealed class FlyToolManifestDto
    {
        public string? Id { get; set; }
        public string? NameKey { get; set; }
        public string[]? Platforms { get; set; }
    }
}

public sealed record FlyToolDescriptor(string Id, string DisplayName, string[] Platforms);

/// <summary>配置页多选行。</summary>
public sealed class FlyToolSelectItem : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isEnabled = true;
    private string _disabledReason = "";

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string[] Platforms { get; init; }

    public string PlatformsText => string.Join(" / ", Platforms);

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Opacity));
        }
    }

    public string DisabledReason
    {
        get => _disabledReason;
        set
        {
            if (_disabledReason == value) return;
            _disabledReason = value;
            OnPropertyChanged();
        }
    }

    public double Opacity => IsEnabled ? 1.0 : 0.45;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
