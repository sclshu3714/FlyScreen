using FlyScreen.Services;
using System.Text.Json;

namespace FlyScreen.Tool.SharedDevices;

/// <summary>提供方本机选用的扬声器/麦克风/摄像头 Id（不进 BehaviorSync）。</summary>
public sealed class SharedDevicesSelectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly string _path;
    private SelectionFile _data = new();

    public SharedDevicesSelectionStore(string? path = null)
    {
        _path = path ?? FlyScreenDataPaths.SharedDevicesSelectionFile;
        Reload();
    }

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    _data = JsonSerializer.Deserialize<SelectionFile>(json, JsonOptions) ?? new();
                }
                else
                    _data = new SelectionFile();
            }
            catch
            {
                _data = new SelectionFile();
            }
        }
    }

    public string? Get(string kind)
    {
        var key = NormKind(kind);
        if (key.Length == 0) return null;
        lock (_gate)
            return _data.ByKind.TryGetValue(key, out var id) ? id : null;
    }

    public void Set(string kind, string? deviceId)
    {
        var key = NormKind(kind);
        if (key.Length == 0) return;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                _data.ByKind.Remove(key);
            else
                _data.ByKind[key] = deviceId.Trim();
            SaveUnlocked();
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn("保存共享设备选用失败: " + ex.Message);
        }
    }

    private static string NormKind(string? kind) => (kind ?? "").Trim().ToLowerInvariant();

    private sealed class SelectionFile
    {
        public Dictionary<string, string> ByKind { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
