using System.Collections.Concurrent;

namespace FlyScreen.Services.Programs;

/// <summary>
/// 设置页按钮 / 表格单元格按钮 → 程序运行时消费。
/// 按 programId 排队；BindSettings 时不自动清空，ClearSettings 时清空。
/// </summary>
public static class ProgramSettingEventBus
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<ProgramSettingEvent>> Queues =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Enqueue(string? programId, string eventName, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(programId) || string.IsNullOrWhiteSpace(eventName))
            return;
        var q = Queues.GetOrAdd(programId.Trim(), _ => new ConcurrentQueue<ProgramSettingEvent>());
        q.Enqueue(new ProgramSettingEvent(
            eventName.Trim(),
            detail?.Trim() ?? "",
            DateTime.UtcNow.Ticks));
    }

    public static bool TryDequeue(string? programId, out ProgramSettingEvent ev)
    {
        ev = new ProgramSettingEvent("", "", 0);
        if (string.IsNullOrWhiteSpace(programId)) return false;
        if (!Queues.TryGetValue(programId.Trim(), out var q)) return false;
        return q.TryDequeue(out ev!);
    }

    public static bool TryDequeue(string? programId, string eventName, out ProgramSettingEvent ev)
    {
        ev = new ProgramSettingEvent("", "", 0);
        if (string.IsNullOrWhiteSpace(programId) || string.IsNullOrWhiteSpace(eventName))
            return false;
        if (!Queues.TryGetValue(programId.Trim(), out var q)) return false;

        // 线性扫描：取出匹配项，其它重新入队（队列通常很短）
        var keep = new List<ProgramSettingEvent>();
        ProgramSettingEvent? found = null;
        while (q.TryDequeue(out var item))
        {
            if (found == null
                && string.Equals(item.Name, eventName.Trim(), StringComparison.OrdinalIgnoreCase))
                found = item;
            else
                keep.Add(item);
        }
        foreach (var k in keep)
            q.Enqueue(k);
        if (found == null) return false;
        ev = found;
        return true;
    }

    public static void Clear(string? programId)
    {
        if (string.IsNullOrWhiteSpace(programId)) return;
        if (Queues.TryRemove(programId.Trim(), out var q))
        {
            while (q.TryDequeue(out _)) { }
        }
    }

    public static int Count(string? programId)
    {
        if (string.IsNullOrWhiteSpace(programId)) return 0;
        return Queues.TryGetValue(programId.Trim(), out var q) ? q.Count : 0;
    }
}
