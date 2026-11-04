using System.IO;
using System.Reflection;
using System.Text.Json;

namespace FlyScreen.Installer.Services;

public sealed class InstallerManifest
{
    public string ProductName { get; set; } = "玄鸟";
    public string PublisherName { get; set; } = "玄鸟";
    public string Version { get; set; } = "1.0.0";
    public string ExeName { get; set; } = "FlyScreen.exe";
    public string DefaultInstallDir { get; set; } = "";
    public string LicenseUrl { get; set; } = "";
    public long RequiredBytes { get; set; } = 200L * 1024 * 1024;
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;
    public string[] CarouselImages { get; set; } = [];
}

public static class ManifestLoader
{
    public static InstallerManifest Load()
    {
        InstallerManifest? manifest = null;
        foreach (var path in CandidatePaths(
                     Path.Combine("Assets", "installer.json"),
                     "installer.json"))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                manifest = JsonSerializer.Deserialize<InstallerManifest>(json);
                if (manifest != null) break;
            }
            catch
            {
                // continue
            }
        }

        manifest ??= CreateDefault();
        manifest.Version = VersionResolver.Resolve(manifest);
        if (string.IsNullOrWhiteSpace(manifest.DefaultInstallDir))
        {
            manifest.DefaultInstallDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                manifest.ProductName);
        }

        return manifest;
    }

    public static string? FindPayloadZip()
    {
        foreach (var path in CandidatePaths(Path.Combine("Payload", "payload.zip"), "payload.zip"))
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static string? FindLicenseText()
    {
        foreach (var path in CandidatePaths(Path.Combine("Assets", "license.txt"), "license.txt"))
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static IEnumerable<string> ResolveCarouselImages(InstallerManifest manifest)
    {
        var found = new List<string>();
        var names = manifest.CarouselImages is { Length: > 0 }
            ? manifest.CarouselImages
            : ["01-welcome.png", "02-sync.png", "03-secure.png"];

        foreach (var name in names)
        {
            foreach (var path in CandidatePaths(
                         Path.Combine("Assets", "carousel", name),
                         Path.Combine("carousel", name),
                         name))
            {
                if (!File.Exists(path)) continue;
                found.Add(path);
                break;
            }
        }

        if (found.Count > 0)
            return found;

        // fallback: any png in carousel folders
        foreach (var dir in CandidateDirs(
                     Path.Combine("Assets", "carousel"),
                     "carousel"))
        {
            if (!Directory.Exists(dir)) continue;
            return Directory.EnumerateFiles(dir, "*.png").OrderBy(x => x).ToList();
        }

        return found;
    }

    private static InstallerManifest CreateDefault() => new()
    {
        DefaultInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "玄鸟"),
        LicenseUrl = "https://github.com/sclshu3714/FlyScreen",
        Version = VersionResolver.Resolve()
    };

    private static IEnumerable<string> CandidatePaths(params string[] relatives)
    {
        var bases = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var b in bases)
        foreach (var r in relatives)
            yield return Path.GetFullPath(Path.Combine(b, r));
    }

    private static IEnumerable<string> CandidateDirs(params string[] relatives)
        => CandidatePaths(relatives);
}
