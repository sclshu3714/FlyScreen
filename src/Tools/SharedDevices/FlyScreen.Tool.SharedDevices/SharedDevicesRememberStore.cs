using FlyScreen.Services;
using System.Text.Json;

namespace FlyScreen.Tool.SharedDevices;

/// <summary>接收端「记住适配」：申请端机器 + 种类（不进 BehaviorSync）。</summary>
public sealed class SharedDevicesRememberStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly string _path;
    private RememberFile _data = new();

    public SharedDevicesRememberStore(string? path = null)
    {
        _path = path ?? FlyScreenDataPaths.SharedDevicesRememberFile;
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
                    _data = JsonSerializer.Deserialize<RememberFile>(json, JsonOptions) ?? new();
                }
                else
                    _data = new RememberFile();
            }
            catch
            {
                _data = new RememberFile();
            }
        }
    }

    public bool IsRemembered(string applicantPeer, IEnumerable<string> kinds)
    {
        var key = Norm(applicantPeer);
        var need = kinds.Select(Norm).Where(k => k.Length > 0).Distinct().ToList();
        if (need.Count == 0) return false;

        lock (_gate)
        {
            var entry = _data.Entries.FirstOrDefault(e =>
                string.Equals(e.Applicant, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;
            return need.All(k =>
                entry.Kinds.Any(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public void Remember(string applicantPeer, IEnumerable<string> kinds)
    {
        var key = Norm(applicantPeer);
        var add = kinds.Select(Norm).Where(k => k.Length > 0).Distinct().ToList();
        if (string.IsNullOrEmpty(key) || add.Count == 0) return;

        lock (_gate)
        {
            var entry = _data.Entries.FirstOrDefault(e =>
                string.Equals(e.Applicant, key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new RememberEntry { Applicant = key };
                _data.Entries.Add(entry);
            }

            foreach (var k in add)
            {
                if (!entry.Kinds.Any(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)))
                    entry.Kinds.Add(k);
            }

            SaveUnlocked();
        }
    }

    public void Clear(string? applicantPeer = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(applicantPeer))
                _data.Entries.Clear();
            else
            {
                var key = Norm(applicantPeer);
                _data.Entries.RemoveAll(e =>
                    string.Equals(e.Applicant, key, StringComparison.OrdinalIgnoreCase));
            }

            SaveUnlocked();
        }
    }

    public IReadOnlyList<(string Applicant, string[] Kinds)> ListEntries()
    {
        lock (_gate)
        {
            return _data.Entries
                .Select(e => (e.Applicant, e.Kinds.ToArray()))
                .ToList();
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
            AppLog.Warn("保存记住适配失败: " + ex.Message);
        }
    }

    private static string Norm(string? s) => (s ?? "").Trim();

    private sealed class RememberFile
    {
        public List<RememberEntry> Entries { get; set; } = [];
    }

    private sealed class RememberEntry
    {
        public string Applicant { get; set; } = "";
        public List<string> Kinds { get; set; } = [];
    }
}
