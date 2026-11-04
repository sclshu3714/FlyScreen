using FlyScreen.Setup.Models;

namespace FlyScreen.Setup.Services;

public static class TargetCatalog
{
    public static IReadOnlyList<PublishTargetItem> CreateDefaults() =>
    [
        new PublishTargetItem
        {
            Id = "win",
            DisplayName = "Windows 桌面 (WPF)",
            Rid = "win-x64",
            Kind = "Exe",
            ProjectPath = @"src\FlyScreen.App.Windows\FlyScreen.App.Windows.csproj",
            SupportsInstaller = true,
            UsesWindowsSetup = true,
            IsSelected = true
        },
        new PublishTargetItem
        {
            Id = "maui-win",
            DisplayName = "MAUI Windows",
            Rid = "maui-win-x64",
            Kind = "Exe",
            ProjectPath = @"src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj",
            Framework = "net10.0-windows10.0.19041.0",
            ExtraArgs = "-p:FlyScreenMauiTfms=windows -p:RuntimeIdentifierOverride=win-x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -p:IncludeAllContentForSelfExtract=true",
            SupportsInstaller = true,
            UsesWindowsSetup = true
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
            SupportsSelfContained = false
        },
        new PublishTargetItem
        {
            Id = "linux",
            DisplayName = "Linux (Avalonia)",
            Rid = "linux-x64",
            Kind = "UnixBin",
            ProjectPath = @"src\FlyScreen.App.Linux\FlyScreen.App.Linux.csproj",
            SupportsInstaller = false
        },
        new PublishTargetItem
        {
            Id = "mac",
            DisplayName = "Mac (Avalonia)",
            Rid = "osx-arm64",
            Kind = "UnixBin",
            ProjectPath = @"src\FlyScreen.App.Mac\FlyScreen.App.Mac.csproj",
            RequiresMac = true,
            SupportsInstaller = false
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
            SupportsInstaller = false
        }
    ];
}
