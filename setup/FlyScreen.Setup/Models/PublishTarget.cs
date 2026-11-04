using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace FlyScreen.Setup.Models;

public enum PackageMode
{
    Standalone,
    Installer
}

public enum DeploymentMode
{
    SelfContained,
    FrameworkDependent
}

public sealed class PublishTargetItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isEnabled = true;
    private string _disabledReason = "";

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Rid { get; init; }
    public required string Kind { get; init; }
    public required string ProjectPath { get; init; }
    public string? Framework { get; init; }
    public string? ExtraArgs { get; init; }
    public bool RequiresMac { get; init; }
    /// <summary>可选「安装包」模式（Windows Setup 或 Android APK）。</summary>
    public bool SupportsInstaller { get; init; }
    /// <summary>走 Windows 自定义 Setup.exe；Android APK 为 false。</summary>
    public bool UsesWindowsSetup { get; init; }
    /// <summary>可选「独立应用」目录产物；Android APK 为 false。</summary>
    public bool SupportsStandalone { get; init; } = true;
    public bool SupportsSelfContained { get; init; } = true;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
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

    public double Opacity => _isEnabled ? 1.0 : 0.45;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PublishOptions
{
    public PackageMode PackageMode { get; set; } = PackageMode.Standalone;
    public DeploymentMode DeploymentMode { get; set; } = DeploymentMode.SelfContained;
    public string Configuration { get; set; } = "Release";
    public string OutputRoot { get; set; } = "";
    public string ProductName { get; set; } = "玄鸟";
    public string PublisherName { get; set; } = "玄鸟";
    public string DefaultInstallDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "玄鸟");
    public string LicenseUrl { get; set; } = "https://github.com/sclshu3714/FlyScreen";
    public string LicenseFile { get; set; } = "";
    public string CarouselDir { get; set; } = "";
    public string ExeName { get; set; } = "FlyScreen.exe";
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;
}

public sealed class InstallerManifest
{
    public string ProductName { get; set; } = "玄鸟";
    public string PublisherName { get; set; } = "玄鸟";
    public string Version { get; set; } = "1.0.0";
    public string ExeName { get; set; } = "FlyScreen.exe";
    public string DefaultInstallDir { get; set; } = "";
    public string LicenseUrl { get; set; } = "";
    public long RequiredBytes { get; set; }
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;
    public string[] CarouselImages { get; set; } = [];
}
