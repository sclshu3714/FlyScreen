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

/// <summary>某打包目标下可选的目标框架及其运行时。</summary>
public sealed class FrameworkChoice
{
    public required string Framework { get; init; }
    public required IReadOnlyList<string> Runtimes { get; init; }
    /// <summary>选中该框架时的额外 publish 参数；可用 {rid} 占位。</summary>
    public string? ExtraArgsTemplate { get; init; }
}

public sealed class PublishTargetItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isEnabled = true;
    private string _disabledReason = "";
    private string? _selectedFramework;
    private string? _selectedRuntime;
    private string _rid = "";

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>产物目录 / 桌面端 -r 用 RID；可随「目标运行时」更新。</summary>
    public required string Rid
    {
        get => _rid;
        set
        {
            if (_rid == value) return;
            _rid = value;
            OnPropertyChanged();
        }
    }
    public required string Kind { get; init; }
    public required string ProjectPath { get; init; }
    public string? Framework { get; set; }
    public string? ExtraArgs { get; set; }
    public bool RequiresMac { get; init; }
    /// <summary>可选「安装包」模式（Windows Setup、Android APK 或 Linux tar.gz）。</summary>
    public bool SupportsInstaller { get; init; }
    /// <summary>走 Windows 自定义 Setup.exe；Android APK / Linux tar.gz 为 false。</summary>
    public bool UsesWindowsSetup { get; init; }
    /// <summary>安装包为目录打成的 .tar.gz（Linux），不是 Setup.exe / APK。</summary>
    public bool UsesArchiveInstaller { get; init; }
    /// <summary>可选「独立应用」目录产物；Android APK 为 false。</summary>
    public bool SupportsStandalone { get; init; } = true;
    /// <summary>可打入虚拟显示离线包。仅 Windows 电脑端（WPF / MAUI Win）。</summary>
    public bool SupportsVddOffline { get; init; }
    public bool SupportsSelfContained { get; init; } = true;

    /// <summary>该端可选框架及对应运行时（供配置页级联下拉）。</summary>
    public IReadOnlyList<FrameworkChoice> FrameworkChoices { get; init; } = [];

    public string? SelectedFramework
    {
        get => _selectedFramework;
        set
        {
            if (_selectedFramework == value) return;
            _selectedFramework = value;
            OnPropertyChanged();
        }
    }

    public string? SelectedRuntime
    {
        get => _selectedRuntime;
        set
        {
            if (_selectedRuntime == value) return;
            _selectedRuntime = value;
            OnPropertyChanged();
        }
    }

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

    /// <summary>把界面选中的框架/运行时写回 publish 用字段。</summary>
    public void ApplySelectedFrameworkRuntime()
    {
        var fw = SelectedFramework;
        var rt = SelectedRuntime;
        if (string.IsNullOrWhiteSpace(fw)) return;

        Framework = fw;
        var choice = FrameworkChoices.FirstOrDefault(c =>
            string.Equals(c.Framework, fw, StringComparison.OrdinalIgnoreCase));
        if (choice == null) return;

        if (!string.IsNullOrWhiteSpace(rt))
        {
            switch (Id)
            {
                case "win":
                case "linux":
                case "mac":
                    Rid = rt;
                    break;
                case "maui-win":
                    // 产物目录用逻辑名 maui-win-x64；真实 RID 写在 ExtraArgs
                    Rid = "maui-" + rt;
                    break;
                case "maui-mac":
                    Rid = "maui-" + rt.Replace("maccatalyst-", "osx-", StringComparison.OrdinalIgnoreCase);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(choice.ExtraArgsTemplate))
                ExtraArgs = choice.ExtraArgsTemplate.Replace("{rid}", rt, StringComparison.OrdinalIgnoreCase);
            else if (Id == "maui-android")
                ExtraArgs = $"-p:AndroidPackageFormat=apk -p:RuntimeIdentifier={rt}";
        }
        else if (!string.IsNullOrWhiteSpace(choice.ExtraArgsTemplate))
        {
            ExtraArgs = choice.ExtraArgsTemplate.Replace("{rid}", GuessDefaultRid(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private string GuessDefaultRid()
    {
        if (Id == "maui-win" && Rid.StartsWith("maui-", StringComparison.OrdinalIgnoreCase))
            return Rid["maui-".Length..];
        return Rid;
    }

    /// <summary>入队时快照，避免后续改 UI 影响已排队任务。</summary>
    public PublishTargetItem CloneForPublish()
    {
        ApplySelectedFrameworkRuntime();
        return new PublishTargetItem
        {
            Id = Id,
            DisplayName = DisplayName,
            Rid = Rid,
            Kind = Kind,
            ProjectPath = ProjectPath,
            Framework = Framework,
            ExtraArgs = ExtraArgs,
            RequiresMac = RequiresMac,
            SupportsInstaller = SupportsInstaller,
            UsesWindowsSetup = UsesWindowsSetup,
            UsesArchiveInstaller = UsesArchiveInstaller,
            SupportsStandalone = SupportsStandalone,
            SupportsVddOffline = SupportsVddOffline,
            SupportsSelfContained = SupportsSelfContained,
            FrameworkChoices = FrameworkChoices,
            SelectedFramework = SelectedFramework,
            SelectedRuntime = SelectedRuntime,
            IsSelected = true,
            IsEnabled = true
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PublishOptions
{
    public PackageMode PackageMode { get; set; } = PackageMode.Installer;
    public DeploymentMode DeploymentMode { get; set; } = DeploymentMode.SelfContained;
    public string Configuration { get; set; } = "Release";
    public string OutputRoot { get; set; } = "";
    public string ProductName { get; set; } = "玄鸟";
    public string PublisherName { get; set; } = "玄鸟";
    public string DefaultInstallDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "XuanNiao");
    public string LicenseUrl { get; set; } = "https://github.com/sclshu3714/FlyScreen";
    public string LicenseFile { get; set; } = "";
    public string CarouselDir { get; set; } = "";
    public string ExeName { get; set; } = "FlyScreen.exe";
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    /// <summary>
    /// 是否把 VddOffline\FlyScreen.VddOffline.zip 打进 Win 产物；关闭则体积更小，运行时走 winget/在线安装。
    /// </summary>
    public bool IncludeVddOffline { get; set; }

    public PublishOptions Clone() => new()
    {
        PackageMode = PackageMode,
        DeploymentMode = DeploymentMode,
        Configuration = Configuration,
        OutputRoot = OutputRoot,
        ProductName = ProductName,
        PublisherName = PublisherName,
        DefaultInstallDir = DefaultInstallDir,
        LicenseUrl = LicenseUrl,
        LicenseFile = LicenseFile,
        CarouselDir = CarouselDir,
        ExeName = ExeName,
        CreateDesktopShortcut = CreateDesktopShortcut,
        CreateStartMenuShortcut = CreateStartMenuShortcut,
        IncludeVddOffline = IncludeVddOffline
    };
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
    public string[] CarouselImages { get; set; } = [];
}
