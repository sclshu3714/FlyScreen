using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyScreen.Services.Tools;

/// <summary>工具页「本地运行程序」登记（无安装/卸载，仅引用可执行路径）。</summary>
public sealed class ToolLocalProgramRecord
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    /// <summary>图标键（写入 <see cref="ToolIconRegistry"/>）。</summary>
    public string IconKey { get; set; } = "";
    /// <summary>PNG Base64（启动时灌入图标缓存）。</summary>
    public string? IconPngBase64 { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class ToolLocalProgramStateFile
{
    public List<ToolLocalProgramRecord> Programs { get; set; } = [];
}

/// <summary>持久化于 <c>Tools/tools-local-programs.json</c>。</summary>
public sealed class ToolLocalProgramCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private List<ToolLocalProgramRecord> _items = [];

    public ToolLocalProgramCatalog()
        => Reload();

    public IReadOnlyList<ToolLocalProgramRecord> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public void Reload()
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        lock (_gate)
        {
            _items = LoadFromDisk();
            foreach (var r in _items)
                RegisterIcon(r);
        }
    }

    public ToolLocalProgramRecord? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
            return _items.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public ToolLocalProgramRecord? FindByPath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        string full;
        try { full = Path.GetFullPath(executablePath.Trim()); }
        catch { return null; }

        lock (_gate)
        {
            return _items.FirstOrDefault(x =>
            {
                try
                {
                    return string.Equals(
                        Path.GetFullPath(x.ExecutablePath),
                        full,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });
        }
    }

    public void Upsert(ToolLocalProgramRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Id))
            throw new ArgumentException("Id 不能为空", nameof(record));

        lock (_gate)
        {
            var idx = _items.FindIndex(x =>
                string.Equals(x.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                _items[idx] = record;
            else
                _items.Add(record);
            RegisterIcon(record);
            SaveUnlocked();
        }
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            var hit = _items.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return false;
            _items.Remove(hit);
            if (!string.IsNullOrWhiteSpace(hit.IconKey))
                ToolIconRegistry.Unregister(hit.IconKey);
            SaveUnlocked();
            return true;
        }
    }

    public static string MakeId(string executablePath)
    {
        var norm = NormalizePath(executablePath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
        return "local:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string MakeIconKey(string id)
        => "local-icon:" + id.Trim();

    public static string SuggestDisplayName(string executablePath)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(executablePath);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                return vi.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(vi.ProductName))
                return vi.ProductName.Trim();
        }
        catch
        {
            /* ignore */
        }

        var name = Path.GetFileNameWithoutExtension(executablePath);
        return string.IsNullOrWhiteSpace(name) ? Path.GetFileName(executablePath) : name;
    }

    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        if (OperatingSystem.IsWindows())
            full = full.TrimEnd('\\', '/').ToLowerInvariant();
        else
            full = full.TrimEnd('/');
        return full;
    }

    private List<ToolLocalProgramRecord> LoadFromDisk()
    {
        try
        {
            var file = FlyScreenDataPaths.ToolsLocalProgramsFile;
            if (!File.Exists(file))
                return [];
            var json = File.ReadAllText(file);
            var state = JsonSerializer.Deserialize<ToolLocalProgramStateFile>(json, JsonOptions);
            return state?.Programs?
                       .Where(p => !string.IsNullOrWhiteSpace(p.Id)
                                   && !string.IsNullOrWhiteSpace(p.ExecutablePath))
                       .ToList()
                   ?? [];
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取本地程序清单失败: " + ex.Message);
            return [];
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            var file = FlyScreenDataPaths.ToolsLocalProgramsFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var state = new ToolLocalProgramStateFile { Programs = _items.ToList() };
            File.WriteAllText(file, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn("保存本地程序清单失败: " + ex.Message);
        }
    }

    private static void RegisterIcon(ToolLocalProgramRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.IconKey) || string.IsNullOrWhiteSpace(r.IconPngBase64))
            return;
        try
        {
            var bytes = Convert.FromBase64String(r.IconPngBase64);
            if (bytes.Length > 0)
                ToolIconRegistry.RegisterBitmap(r.IconKey, bytes);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"注册本地程序图标失败 {r.Id}: {ex.Message}");
        }
    }
}
