using System.Text.Json;

namespace FlyScreen.Services.Tools;

/// <summary>工具页快捷栏槽位（tools-shortcuts.json）。靠左紧凑；末尾由 UI 显示「拖拽到此」。</summary>
public sealed class ToolShortcutsStore
{
    /// <summary>最多固定数量（6–8，取 8）。</summary>
    public const int SlotCount = 8;

    private readonly object _gate = new();
    private readonly string _path;
    private string?[] _slots = new string?[SlotCount];

    public event Action? Changed;

    public ToolShortcutsStore(string? path = null)
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        _path = path ?? FlyScreenDataPaths.ToolsShortcutsFile;
        Reload();
    }

    /// <summary>固定长度槽位（中间可能有空洞；UI 应优先用 <see cref="Occupied"/>）。</summary>
    public IReadOnlyList<string?> Slots
    {
        get { lock (_gate) return _slots.ToArray(); }
    }

    /// <summary>已固定工具 Id，靠左紧凑、无空洞。</summary>
    public IReadOnlyList<string> Occupied
    {
        get
        {
            lock (_gate)
                return _slots.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
        }
    }

    public int OccupiedCount
    {
        get
        {
            lock (_gate)
                return _slots.Count(s => !string.IsNullOrWhiteSpace(s));
        }
    }

    public bool IsFull => OccupiedCount >= SlotCount;

    /// <summary>下一个可放入的下标；已满返回 -1。</summary>
    public int NextEmptyIndex
    {
        get
        {
            lock (_gate)
            {
                CompactUnlocked();
                for (var i = 0; i < SlotCount; i++)
                {
                    if (string.IsNullOrWhiteSpace(_slots[i]))
                        return i;
                }

                return -1;
            }
        }
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
                    var file = JsonSerializer.Deserialize<ToolShortcutsFile>(json, ToolPackageJson.Options);
                    _slots = new string?[SlotCount];
                    if (file?.Slots != null)
                    {
                        var n = 0;
                        foreach (var s in file.Slots)
                        {
                            if (n >= SlotCount) break;
                            if (string.IsNullOrWhiteSpace(s)) continue;
                            _slots[n++] = s.Trim();
                        }
                    }
                }
                else
                {
                    _slots = new string?[SlotCount];
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("读取工具快捷栏失败: " + ex.Message);
                _slots = new string?[SlotCount];
            }
        }

        Changed?.Invoke();
    }

    public void SetSlot(int index, string? toolId)
    {
        if (index < 0 || index >= SlotCount) return;
        lock (_gate)
        {
            _slots[index] = string.IsNullOrWhiteSpace(toolId) ? null : toolId.Trim();
            CompactUnlocked();
            PersistUnlocked();
        }

        Changed?.Invoke();
    }

    /// <summary>追加到末尾；已满或重复则 false。</summary>
    public bool TryAppend(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return false;
        toolId = toolId.Trim();
        lock (_gate)
        {
            CompactUnlocked();
            if (_slots.Any(s => string.Equals(s, toolId, StringComparison.OrdinalIgnoreCase)))
                return true;

            for (var i = 0; i < SlotCount; i++)
            {
                if (!string.IsNullOrWhiteSpace(_slots[i])) continue;
                _slots[i] = toolId;
                PersistUnlocked();
                Changed?.Invoke();
                return true;
            }

            return false;
        }
    }

    public void ClearTool(string toolId)
    {
        lock (_gate)
        {
            var changed = false;
            for (var i = 0; i < _slots.Length; i++)
            {
                if (string.Equals(_slots[i], toolId, StringComparison.OrdinalIgnoreCase))
                {
                    _slots[i] = null;
                    changed = true;
                }
            }

            if (!changed) return;
            CompactUnlocked();
            PersistUnlocked();
        }

        Changed?.Invoke();
    }

    private void CompactUnlocked()
    {
        var packed = _slots.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        _slots = new string?[SlotCount];
        for (var i = 0; i < packed.Length && i < SlotCount; i++)
            _slots[i] = packed[i];
    }

    private void PersistUnlocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // 只持久化已占用项，便于日后改 SlotCount
            var file = new ToolShortcutsFile
            {
                Slots = _slots.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(file, ToolPackageJson.Options));
        }
        catch (Exception ex)
        {
            AppLog.Warn("保存工具快捷栏失败: " + ex.Message);
        }
    }

    private sealed class ToolShortcutsFile
    {
        public List<string> Slots { get; set; } = [];
    }
}
