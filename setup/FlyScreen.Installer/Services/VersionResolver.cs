using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.Installer.Services;

/// <summary>
/// 解析真实产品版本：安装清单 → build/version.json → 程序集 InformationalVersion。
/// </summary>
public static class VersionResolver
{
    public static string Resolve(InstallerManifest? manifest = null)
    {
        // 优先仓库/输出目录中的 version.json（与各端统一版本源一致）
        var fromJson = TryReadVersionJson();
        if (fromJson != null)
            return fromJson;

        if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Version))
            return Normalize(manifest.Version);

        var fromAsm = TryReadAssemblyVersion();
        if (fromAsm != null)
            return fromAsm;

        return "0.0.0";
    }

    public static string FormatDisplay(string version)
    {
        var v = Normalize(version);
        return string.IsNullOrWhiteSpace(v) ? "" : $"v{v}";
    }

    private static string Normalize(string version)
    {
        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V'))
            v = v[1..];
        // 去掉可能的四段 Revision：1.0.8.0 → 1.0.8
        var m = Regex.Match(v, @"^\d+\.\d+\.\d+");
        return m.Success ? m.Value : v;
    }

    private static string? TryReadVersionJson()
    {
        foreach (var path in VersionJsonCandidates())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("version", out var prop))
                {
                    var v = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return Normalize(v);
                }
            }
            catch
            {
                // continue
            }
        }

        return null;
    }

    private static string? TryReadAssemblyVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // 可能带 +gitsha
            var cleaned = info.Split('+', StringSplitOptions.RemoveEmptyEntries)[0];
            if (Regex.IsMatch(cleaned, @"\d+\.\d+\.\d+"))
                return Normalize(cleaned);
        }

        var ver = asm.GetName().Version;
        if (ver != null)
            return $"{ver.Major}.{ver.Minor}.{Math.Max(ver.Build, 0)}";

        return null;
    }

    private static IEnumerable<string> VersionJsonCandidates()
    {
        var bases = new List<string>
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory
        };

        // 开发期：从 bin 向上找仓库 build/version.json
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            bases.Add(dir.FullName);
            var buildJson = Path.Combine(dir.FullName, "build", "version.json");
            if (File.Exists(buildJson))
                yield return buildJson;
            if (File.Exists(Path.Combine(dir.FullName, "FlyScreen.sln")))
                break;
        }

        foreach (var b in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(b, "version.json");
            yield return Path.Combine(b, "Assets", "version.json");
            yield return Path.Combine(b, "build", "version.json");
        }
    }
}
