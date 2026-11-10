using System.Text.Json.Serialization;

namespace FlyScreen.Services.Tools;

public enum ToolInstallStatus
{
    NotInstalled,
    Installed,
    NeedsRepair
}

public enum ToolTrustLevel
{
    Bundled,
    KnownRelease,
    Unknown
}

/// <summary>工具清单条目（内置或包内 manifest）。</summary>
public sealed class ToolManifest
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "enhancement";
    public bool CrossScreen { get; set; }
    public string Group { get; set; } = "";
    public string NameKey { get; set; } = "";
    public string SummaryKey { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public string MinAppVersion { get; set; } = "1.0.0";
    public string[] DeviceClasses { get; set; } = ["Desktop", "Phone", "Tablet"];
    public string[] Platforms { get; set; } = ["Windows", "Android", "macOS", "Linux"];
    public Dictionary<string, string[]>? FeaturesByDeviceClass { get; set; }
    public string[] Roles { get; set; } = ["request", "authorize"];
    public bool SupportsRepair { get; set; }
    public string[] CapNames { get; set; } = [];
    public bool TitleBar { get; set; }
    /// <summary>启动后主界面嵌入工具页工作区（非独立弹窗）。</summary>
    public bool HasWorkspace { get; set; }
    public string[] Permissions { get; set; } = [];
    public string PackageVersion { get; set; } = "1.0.0";
    /// <summary>卡片图标键（如 tool-color-picker）；宿主按键解析 BrandAssets / 矢量。</summary>
    public string Icon { get; set; } = "";
    /// <summary>入口程序集名（无 .dll），进程内/ALC 加载用。</summary>
    public string EntryAssembly { get; set; } = "";
    /// <summary>实现 IToolModule 的类型全名。</summary>
    public string EntryType { get; set; } = "";
}

/// <summary>本机安装记录（tools-install.json）。</summary>
public sealed class ToolInstallRecord
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string TrustLevel { get; set; } = nameof(ToolTrustLevel.Bundled);
    public DateTimeOffset InstalledAt { get; set; }
}

public sealed class ToolInstallStateFile
{
    public List<ToolInstallRecord> Tools { get; set; } = [];
}

/// <summary>工具页卡片视图模型。</summary>
public sealed class ToolCardVm
{
    public required ToolManifest Manifest { get; init; }
    public ToolInstallStatus Status { get; set; }
    public string? InstalledVersion { get; set; }
    public bool IsRunning { get; set; }
    public bool CanConsume { get; set; }
    public bool CanProvide { get; set; }

    [JsonIgnore]
    public string Id => Manifest.Id;
}
