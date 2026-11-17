using System.IO;
using System.Text.Json;

namespace FlyScreen.Setup.Services;

/// <summary>Setup 窗口 UI 偏好（本机用户级，下次打开恢复）。</summary>
public sealed class SetupUiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string DockerHost { get; set; } = "";
    public string DockerPort { get; set; } = "2375";
    public string DockerAuthMode { get; set; } = "tcp";
    public string DockerUser { get; set; } = "";

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlyScreen", "Setup", "ui-settings.json");

    public static SetupUiSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return new SetupUiSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SetupUiSettings>(json, JsonOptions) ?? new SetupUiSettings();
        }
        catch
        {
            return new SetupUiSettings();
        }
    }

    public void Save()
    {
        var path = SettingsPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
