using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyScreen.Services.Tools;

/// <summary>.flytool / 安装目录内 manifest.json。</summary>
public sealed class ToolPackageManifest
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
    public bool HasWorkspace { get; set; }
    public string[] Permissions { get; set; } = [];
    public string Icon { get; set; } = "";

    [JsonPropertyName("package")]
    public ToolPackageInfo? Package { get; set; }

    /// <summary>入口程序集文件名（如 FlyScreen.Tool.ColorPicker.dll）。</summary>
    public string EntryAssembly { get; set; } = "";

    /// <summary>实现 IToolModule 的类型全名。</summary>
    public string EntryType { get; set; } = "";

    public string PackageVersion => Package?.Version ?? "1.0.0";

    public ToolManifest ToToolManifest()
        => new()
        {
            Id = Id,
            Kind = Kind,
            CrossScreen = CrossScreen,
            Group = Group,
            NameKey = NameKey,
            SummaryKey = SummaryKey,
            SchemaVersion = SchemaVersion,
            MinAppVersion = MinAppVersion,
            DeviceClasses = DeviceClasses,
            Platforms = Platforms,
            FeaturesByDeviceClass = FeaturesByDeviceClass,
            Roles = Roles,
            SupportsRepair = SupportsRepair,
            CapNames = CapNames,
            TitleBar = TitleBar,
            HasWorkspace = HasWorkspace,
            Permissions = Permissions,
            PackageVersion = PackageVersion,
            Icon = Icon,
            EntryAssembly = NormalizeAssemblyName(EntryAssembly),
            EntryType = EntryType
        };

    private static string NormalizeAssemblyName(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return "";
        return entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? entry[..^4]
            : entry;
    }
}

public sealed class ToolPackageInfo
{
    public string Version { get; set; } = "1.0.0";
    public string Format { get; set; } = "flytool";
    public int FormatVersion { get; set; } = 1;
}

public static class ToolPackageJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public static class ToolRuntimeId
{
    public static string Current
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Environment.Is64BitProcess ? "win-x64" : "win-x86";
            if (OperatingSystem.IsMacOS())
                return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    == System.Runtime.InteropServices.Architecture.Arm64
                    ? "osx-arm64"
                    : "osx-x64";
            if (OperatingSystem.IsLinux())
                return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    == System.Runtime.InteropServices.Architecture.Arm64
                    ? "linux-arm64"
                    : "linux-x64";
            if (OperatingSystem.IsAndroid())
                return "android-arm64";
            return "any";
        }
    }
}
