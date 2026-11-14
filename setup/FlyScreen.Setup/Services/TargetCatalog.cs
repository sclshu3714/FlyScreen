using FlyScreen.Setup.Models;

namespace FlyScreen.Setup.Services;

public static class TargetCatalog
{
    public static IReadOnlyList<PublishTargetItem> CreateDefaults()
    {
        PublishTargetItem[] list =
        [
            new PublishTargetItem
            {
                Id = "win",
                DisplayName = "Windows 桌面 (WPF)",
                Rid = "win-x64",
                Kind = "Exe",
                ProjectPath = @"src\FlyScreen.App.Windows\FlyScreen.App.Windows.csproj",
                Framework = "net10.0-windows10.0.19041.0",
                SupportsInstaller = true,
                UsesWindowsSetup = true,
                SupportsVddOffline = true,
                IsSelected = true,
                FrameworkChoices =
                [
                    new FrameworkChoice
                    {
                        Framework = "net10.0-windows10.0.19041.0",
                        Runtimes = ["win-x64", "win-arm64", "win-x86"]
                    }
                ]
            },
            new PublishTargetItem
            {
                Id = "maui-win",
                DisplayName = "MAUI Windows",
                Rid = "maui-win-x64",
                Kind = "Exe",
                ProjectPath = @"src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj",
                Framework = "net10.0-windows10.0.19041.0",
                ExtraArgs =
                    "-p:FlyScreenMauiTfms=windows -p:RuntimeIdentifierOverride=win-x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -p:IncludeAllContentForSelfExtract=true",
                SupportsInstaller = true,
                UsesWindowsSetup = true,
                SupportsVddOffline = true,
                FrameworkChoices =
                [
                    new FrameworkChoice
                    {
                        Framework = "net10.0-windows10.0.19041.0",
                        Runtimes = ["win-x64", "win-arm64"],
                        ExtraArgsTemplate =
                            "-p:FlyScreenMauiTfms=windows -p:RuntimeIdentifierOverride={rid} -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -p:IncludeAllContentForSelfExtract=true"
                    }
                ]
            },
            new PublishTargetItem
            {
                Id = "maui-android",
                DisplayName = "MAUI Android (APK)",
                Rid = "maui-android",
                Kind = "Apk",
                ProjectPath = @"src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj",
                Framework = "net10.0-android",
                ExtraArgs = "-p:AndroidPackageFormat=apk",
                SupportsInstaller = true,
                UsesWindowsSetup = false,
                SupportsStandalone = false,
                SupportsSelfContained = false,
                FrameworkChoices =
                [
                    new FrameworkChoice
                    {
                        Framework = "net10.0-android",
                        Runtimes = ["android-arm64", "android-arm", "android-x64"],
                        ExtraArgsTemplate = "-p:AndroidPackageFormat=apk -p:RuntimeIdentifier={rid}"
                    }
                ]
            },
            new PublishTargetItem
            {
                Id = "linux",
                DisplayName = "Linux (Avalonia)",
                Rid = "linux-x64",
                Kind = "UnixBin",
                ProjectPath = @"src\FlyScreen.App.Linux\FlyScreen.App.Linux.csproj",
                Framework = "net10.0",
                SupportsInstaller = true,
                UsesWindowsSetup = false,
                UsesArchiveInstaller = true,
                FrameworkChoices =
                [
                    new FrameworkChoice
                    {
                        Framework = "net10.0",
                        Runtimes = ["linux-x64", "linux-arm64"]
                    }
                ]
            },
            new PublishTargetItem
            {
                Id = "mac",
                DisplayName = "Mac (Avalonia)",
                Rid = "osx-arm64",
                Kind = "UnixBin",
                ProjectPath = @"src\FlyScreen.App.Mac\FlyScreen.App.Mac.csproj",
                Framework = "net10.0",
                RequiresMac = true,
                SupportsInstaller = false,
                FrameworkChoices =
                [
                    new FrameworkChoice
                    {
                        Framework = "net10.0",
                        Runtimes = ["osx-arm64", "osx-x64"]
                    }
                ]
            },
            new PublishTargetItem
            {
                Id = "maui-mac",
                DisplayName = "MAUI Mac Catalyst",
                Rid = "maui-osx-arm64",
                Kind = "UnixBin",
                ProjectPath = @"src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj",
                Framework = "net10.0-maccatalyst",
                ExtraArgs = "-r maccatalyst-arm64 -p:CreatePackage=false",
                RequiresMac = true,
                SupportsInstaller = false,
                FrameworkChoices =
                [
                    new FrameworkChoice
                    {
                        Framework = "net10.0-maccatalyst",
                        Runtimes = ["maccatalyst-arm64", "maccatalyst-x64"],
                        ExtraArgsTemplate = "-r {rid} -p:CreatePackage=false"
                    }
                ]
            },
            new PublishTargetItem
            {
                Id = "flytools",
                DisplayName = "插件工具 (.flytool)",
                Rid = "flytools",
                Kind = "FlyTools",
                ProjectPath = @"build\Pack-FlyTools.ps1",
                Framework = "Release",
                SupportsInstaller = true,
                SupportsStandalone = true,
                SupportsSelfContained = false,
                UsesWindowsSetup = false,
                UsesArchiveInstaller = true,
                FrameworkChoices =
                [
                    new FrameworkChoice
                    {
                        Framework = "Release",
                        Runtimes = ["bundled"]
                    }
                ]
            },
            new PublishTargetItem
            {
                Id = "relay",
                DisplayName = "中继服务 (Relay)",
                Rid = "relay-win-x64",
                Kind = "Relay",
                ProjectPath = @"src\FlyScreen.Relay\FlyScreen.Relay.csproj",
                Framework = "net10.0",
                SupportsInstaller = true,
                SupportsStandalone = true,
                SupportsSelfContained = true,
                UsesWindowsSetup = false,
                UsesArchiveInstaller = true,
                FrameworkChoices =
                [
                    new FrameworkChoice
                    {
                        Framework = "net10.0",
                        Runtimes =
                        [
                            "win-x64",
                            "linux-x64",
                            "linux-arm64",
                            "osx-arm64",
                            "osx-x64",
                            "docker"
                        ]
                    }
                ]
            }
        ];

        foreach (var t in list)
            InitSelection(t);

        return list;
    }

    private static void InitSelection(PublishTargetItem t)
    {
        if (t.FrameworkChoices.Count == 0) return;

        var fw = t.FrameworkChoices.FirstOrDefault(c =>
                       string.Equals(c.Framework, t.Framework, StringComparison.OrdinalIgnoreCase))
                   ?? t.FrameworkChoices[0];
        t.SelectedFramework = fw.Framework;

        var preferred = PreferRuntime(t, fw);
        t.SelectedRuntime = fw.Runtimes.FirstOrDefault(r =>
                                string.Equals(r, preferred, StringComparison.OrdinalIgnoreCase))
                            ?? fw.Runtimes.FirstOrDefault();
        t.ApplySelectedFrameworkRuntime();
    }

    private static string? PreferRuntime(PublishTargetItem t, FrameworkChoice fw)
    {
        if (t.Id == "maui-win")
            return ExtractArgValue(t.ExtraArgs, "RuntimeIdentifierOverride=") ?? "win-x64";
        if (t.Id == "maui-android")
            return ExtractArgValue(t.ExtraArgs, "RuntimeIdentifier=") ?? fw.Runtimes.FirstOrDefault();
        if (t.Id == "maui-mac")
            return ExtractArgValue(t.ExtraArgs, "-r ") ?? "maccatalyst-arm64";
        if (t.Id == "flytools")
            return "bundled";
        if (t.Id == "relay")
        {
            if (t.Rid.StartsWith("relay-", StringComparison.OrdinalIgnoreCase))
                return t.Rid["relay-".Length..];
            return OperatingSystem.IsWindows() ? "win-x64"
                : OperatingSystem.IsMacOS() ? "osx-arm64"
                : "linux-x64";
        }
        return t.Rid;
    }

    private static string? ExtractArgValue(string? extraArgs, string key)
    {
        if (string.IsNullOrWhiteSpace(extraArgs)) return null;
        var i = extraArgs.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += key.Length;
        var end = extraArgs.IndexOfAny([' ', '\t'], i);
        return end < 0 ? extraArgs[i..] : extraArgs[i..end];
    }
}
