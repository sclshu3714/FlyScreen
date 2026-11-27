using System.Text.Json;

namespace FlyScreen.Tool.FileVault.Store;

internal sealed class VaultSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public VaultSettingsStore(string toolDataDir)
    {
        Directory.CreateDirectory(toolDataDir);
        _path = Path.Combine(toolDataDir, "settings.json");
    }

    public VaultSettingsDto Load()
    {
        if (!System.IO.File.Exists(_path))
            return new VaultSettingsDto();
        try
        {
            var json = System.IO.File.ReadAllText(_path);
            return JsonSerializer.Deserialize<VaultSettingsDto>(json, JsonOptions) ?? new VaultSettingsDto();
        }
        catch
        {
            return new VaultSettingsDto();
        }
    }

    public void Save(VaultSettingsDto dto)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOptions));
        System.IO.File.Move(tmp, _path, overwrite: true);
    }
}
