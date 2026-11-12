using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.Setup.Services;

public static class VersionResolver
{
    private static readonly Regex SemVer3 = new(
        @"^\d+\.\d+\.\d+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string ReadCurrent(string repoRoot)
    {
        try
        {
            var path = RepoPaths.VersionJson(repoRoot);
            if (!File.Exists(path))
                return "1.0.0";

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("version", out var v))
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }
        catch
        {
            // ignore
        }

        return "1.0.0";
    }

    public static string Normalize(string? version)
    {
        var s = (version ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            throw new InvalidOperationException("版本号不能为空。");
        if (!SemVer3.IsMatch(s))
            throw new InvalidOperationException("版本号格式应为 x.y.z（例如 1.0.11）。");
        return s;
    }

    /// <summary>与 build/FlyScreen.Version.targets 一致：major*10000 + minor*100 + patch。</summary>
    public static int ComputeVersionCode(string version)
    {
        var v = Version.Parse(Normalize(version));
        return v.Major * 10000 + v.Minor * 100 + Math.Max(0, v.Build);
    }
}
