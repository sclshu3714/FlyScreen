using System.Collections.ObjectModel;
using System.Text;

namespace FlyScreen.Services;

public enum AppLogLevel
{
    Info = 0,
    Warn = 1,
    Error = 2
}

public sealed class AppLogEntry
{
    public DateTime Time { get; init; }
    public AppLogLevel Level { get; init; }
    public string Message { get; init; } = "";
    /// <summary>产生这条日志的用户程序。空表示与具体程序无关。</summary>
    public string? ProgramId { get; init; }
    public string? ProgramName { get; init; }

    public string Display
    {
        get
        {
            var tag = Level switch
            {
                AppLogLevel.Error => "错误",
                AppLogLevel.Warn => "警告",
                _ => "信息"
            };
            return $"[{Time:HH:mm:ss}] {tag}  {Message}";
        }
    }
}

/// <summary>进程内日志环（状态消息与错误）。各端日志页读取。</summary>
public static class AppLog
{
    public const int MaxEntries = 400;

    private static readonly object Gate = new();
    private static readonly List<AppLogEntry> Items = new();

    public static event Action? Changed;

    public static void Info(string message, string? programId = null, string? programName = null)
        => Add(AppLogLevel.Info, message, programId, programName);
    public static void Warn(string message, string? programId = null, string? programName = null)
        => Add(AppLogLevel.Warn, message, programId, programName);
    public static void Error(string message, string? programId = null, string? programName = null)
        => Add(AppLogLevel.Error, message, programId, programName);
    public static void Error(Exception ex, string? context = null, string? programId = null, string? programName = null)
    {
        var msg = string.IsNullOrWhiteSpace(context)
            ? ex.Message
            : $"{context}: {ex.Message}";
        Add(AppLogLevel.Error, msg, programId, programName);
    }

    /// <summary>把状态栏文案按关键词归成信息 / 警告 / 错误。</summary>
    public static void RecordStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (LooksLikeError(message))
            Error(message);
        else if (message.Contains("警告", StringComparison.Ordinal)
                 || message.Contains("warn", StringComparison.OrdinalIgnoreCase))
            Warn(message);
        else
            Info(message);
    }

    /// <summary>
    /// 侧栏状态区用的短提示：去掉 Caps / 协议等英文技术尾巴，并截断长度，避免挤占菜单。
    /// 完整原文仍由 <see cref="RecordStatus"/> 写入日志页。
    /// </summary>
    public static string ForStatusBar(string? message, int maxChars = 42)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var s = message.Trim();

        var caps = s.IndexOf(" Caps=", StringComparison.Ordinal);
        if (caps > 0)
            s = s[..caps].TrimEnd(' ', '·', '[', ']');

        var proto = s.IndexOf(" · v", StringComparison.Ordinal);
        if (proto > 0)
            s = s[..proto].TrimEnd();

        // 去掉残留的 " · Windows/…" 一类英文平台串
        var slashPlat = s.IndexOf(" · ", StringComparison.Ordinal);
        if (slashPlat > 0)
        {
            var tail = s[(slashPlat + 3)..];
            if (LooksTechnicalTail(tail))
                s = s[..slashPlat].TrimEnd();
        }

        if (s.Length > maxChars)
            s = s[..(maxChars - 1)].TrimEnd() + "…";
        return s;
    }

    private static bool LooksTechnicalTail(string tail)
        => tail.Contains("Caps=", StringComparison.Ordinal)
           || tail.StartsWith('v') && tail.Length > 1 && char.IsDigit(tail[1])
           || tail.Contains("Windows/", StringComparison.OrdinalIgnoreCase)
           || tail.Contains("MacOS/", StringComparison.OrdinalIgnoreCase)
           || tail.Contains("Linux/", StringComparison.OrdinalIgnoreCase)
           || tail.Contains("Android/", StringComparison.OrdinalIgnoreCase)
           || tail.Contains("Desktop", StringComparison.OrdinalIgnoreCase)
             && (tail.Contains('/') || tail.Contains('['));

    public static IReadOnlyList<AppLogEntry> Snapshot(
        bool errorsOnly = false,
        string? programId = null,
        string? programName = null)
    {
        lock (Gate)
        {
            IEnumerable<AppLogEntry> query = Items;
            if (errorsOnly)
                query = query.Where(e => e.Level == AppLogLevel.Error);
            if (!string.IsNullOrEmpty(programId) || !string.IsNullOrEmpty(programName))
                query = query.Where(e => BelongsToProgram(e, programId, programName));
            return query.ToArray();
        }
    }

    private static bool BelongsToProgram(AppLogEntry entry, string? programId, string? programName)
    {
        if (!string.IsNullOrEmpty(entry.ProgramId))
            return !string.IsNullOrEmpty(programId)
                   && string.Equals(entry.ProgramId, programId, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(programName)) return false;
        if (!string.IsNullOrEmpty(entry.ProgramName)
            && string.Equals(entry.ProgramName, programName, StringComparison.OrdinalIgnoreCase))
            return true;
        return entry.Message.Contains($"「{programName}」", StringComparison.Ordinal)
               || entry.Message.Contains($"[{programName}]", StringComparison.Ordinal);
    }

    public static void Clear()
    {
        lock (Gate)
            Items.Clear();
        try { Changed?.Invoke(); } catch { /* ignore */ }
    }

    /// <summary>
    /// 计算相对 <paramref name="lastWritten"/> 的新增日志文本（仅追加，绝不重放旧行）。
    /// 环缓冲丢掉 last 时只接上 Time 更新的行，避免整表重刷。
    /// </summary>
    public static string? FormatNewLines(IReadOnlyList<AppLogEntry> snapshot, ref AppLogEntry? lastWritten)
    {
        var start = FindAppendStart(snapshot, lastWritten);
        if (start >= snapshot.Count) return null;

        var sb = new StringBuilder();
        for (var i = start; i < snapshot.Count; i++)
        {
            sb.AppendLine(snapshot[i].Display);
            lastWritten = snapshot[i];
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>列表增量追加（不从头部 RemoveAt，避免 ObservableCollection 触发整表重绘）。</summary>
    public static bool SyncTo(
        ObservableCollection<AppLogEntry> ui,
        IReadOnlyList<AppLogEntry> snapshot)
    {
        AppLogEntry? last = ui.Count > 0 ? ui[^1] : null;
        var start = FindAppendStart(snapshot, last);
        var added = false;
        for (var i = start; i < snapshot.Count; i++)
        {
            ui.Add(snapshot[i]);
            added = true;
        }
        return added;
    }

    private static int FindAppendStart(IReadOnlyList<AppLogEntry> snapshot, AppLogEntry? last)
    {
        if (last == null) return 0;

        var idx = -1;
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (ReferenceEquals(snapshot[i], last) || SameEntry(snapshot[i], last))
                idx = i;
        }
        if (idx >= 0) return idx + 1;

        // last 已被环缓冲丢掉：禁止从 0 重放，只接更晚的时间戳
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (snapshot[i].Time > last.Time)
                return i;
        }
        return snapshot.Count;
    }

    private static bool SameEntry(AppLogEntry a, AppLogEntry b)
        => a.Time == b.Time && a.Level == b.Level && a.Message == b.Message;

    private static void Add(AppLogLevel level, string message, string? programId = null, string? programName = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (Gate)
        {
            Items.Add(new AppLogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Message = message.Trim(),
                ProgramId = string.IsNullOrWhiteSpace(programId) ? null : programId,
                ProgramName = string.IsNullOrWhiteSpace(programName) ? null : programName
            });
            if (Items.Count > MaxEntries)
                Items.RemoveRange(0, Items.Count - MaxEntries);
        }

        try { Changed?.Invoke(); } catch { /* ignore */ }
    }

    private static bool LooksLikeError(string message)
        => message.Contains("失败", StringComparison.Ordinal)
           || message.Contains("错误", StringComparison.Ordinal)
           || message.Contains("异常", StringComparison.Ordinal)
           || message.Contains("无法", StringComparison.Ordinal)
           || message.Contains("未授权", StringComparison.Ordinal)
           || message.Contains("error", StringComparison.OrdinalIgnoreCase)
           || message.Contains("fail", StringComparison.OrdinalIgnoreCase)
           || message.Contains("exception", StringComparison.OrdinalIgnoreCase);
}
