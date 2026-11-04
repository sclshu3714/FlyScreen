using System.IO;

namespace FlyScreen.Setup.Services;

public static class RepoPaths
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var sln = Path.Combine(dir.FullName, "FlyScreen.sln");
            var setupMarker = Path.Combine(dir.FullName, "setup", "FlyScreen.Setup");
            if (File.Exists(sln) && Directory.Exists(setupMarker))
                return dir.FullName;

            // Running from setup/FlyScreen.Setup/bin/...
            if (dir.Name.Equals("FlyScreen.Setup", StringComparison.OrdinalIgnoreCase)
                && dir.Parent?.Name.Equals("setup", StringComparison.OrdinalIgnoreCase) == true
                && dir.Parent.Parent != null
                && File.Exists(Path.Combine(dir.Parent.Parent.FullName, "FlyScreen.sln")))
            {
                return dir.Parent.Parent.FullName;
            }

            dir = dir.Parent;
        }

        // Fallback: walk up from BaseDirectory looking for FlyScreen.sln
        dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FlyScreen.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("无法定位仓库根目录（缺少 FlyScreen.sln）。");
    }

    public static string SetupRoot(string repoRoot) => Path.Combine(repoRoot, "setup");
    public static string PublisherDir(string repoRoot) => Path.Combine(SetupRoot(repoRoot), "FlyScreen.Setup");
    public static string InstallerDir(string repoRoot) => Path.Combine(SetupRoot(repoRoot), "FlyScreen.Installer");
    public static string UninstallerDir(string repoRoot) => Path.Combine(SetupRoot(repoRoot), "FlyScreen.Uninstaller");
    public static string VersionJson(string repoRoot) => Path.Combine(repoRoot, "build", "version.json");
    public static string ArtifactsRelease(string repoRoot) => Path.Combine(repoRoot, "artifacts", "release");
}
