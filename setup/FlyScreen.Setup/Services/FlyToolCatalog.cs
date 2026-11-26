using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlyScreen.Setup.Services;

/// <summary>发布工具可选的插件清单（与 build/Pack-FlyTools.ps1 对齐）。</summary>
public static class FlyToolCatalog
{
    public static IReadOnlyList<FlyToolDescriptor> All { get; } =
    [
        new("color-picker", "取色器", ["Windows", "macOS", "Linux"]),
        new("shared-devices", "共享设备", ["Windows", "Android", "macOS", "Linux"]),
        new("port-proxy", "端口代理", ["Windows"]),
        new("video-wall", "视频墙", ["Windows", "Android"]),
        new("developer-tools", "开发者工具", ["Windows", "macOS", "Linux"])
    ];

    /// <summary>打包目标 Id → 清单 platforms 值；null 表示不限（插件组包目标）。</summary>
    public static string? ResolvePlatform(string? targetId) => targetId switch
    {
        "win" or "maui-win" => "Windows",
        "maui-android" => "Android",
        "linux" => "Linux",
        "mac" or "maui-mac" => "macOS",
        "flytools" => null,
        _ => null
    };

    public static bool SupportsPlatform(FlyToolDescriptor tool, string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return true;
        return tool.Platforms.Any(p => p.Equals(platform, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record FlyToolDescriptor(string Id, string DisplayName, string[] Platforms);

/// <summary>配置页多选行。</summary>
public sealed class FlyToolSelectItem : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isEnabled = true;
    private string _disabledReason = "";

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string[] Platforms { get; init; }

    public string PlatformsText => string.Join(" / ", Platforms);

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
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

    public double Opacity => IsEnabled ? 1.0 : 0.45;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
