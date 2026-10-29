using System.Collections.ObjectModel;
using System.Text;

namespace FlyScreen.Services;

public enum AppLogLevel
{
    /// <summary>调试（心跳/静默分支等）；正式运行默认不落盘。</summary>
    Debug = 3,
    Info = 0,
    Warn = 1,
    Error = 2
}

/// <summary>日志页级别筛选：全部 / 调试 / 信息 / 警告 / 错误。</summary>
public enum AppLogLevelFilter
{
    All = -1,
    Info = 0,
    Warn = 1,
    Error = 2,
    Debug = 3
}

/// <summary>日志列表相对水印的增量；<see cref="Reset"/> 时 UI 应替换而非追加。</summary>
public readonly struct AppLogDelta
{
    public AppLogDelta(IReadOnlyList<AppLogEntry> items, bool reset)
    {
        Items = items;
        Reset = reset;
    }

    public IReadOnlyList<AppLogEntry> Items { get; }
    public bool Reset { get; }
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
                AppLogLevel.Debug => "调试",
                _ => "信息"
            };
            return $"[{Time:HH:mm:ss}] {tag}  {Message}";
        }
    }
}

/// <summary>
/// 进程内日志：小环缓冲供实时 UI；落盘按天+5MB 分块、保留 3 天。
/// 日志页请用 <see cref="GetPage"/> 分页，默认最新页，避免一次绑定海量条目卡死。
/// </summary>
public static class AppLog
{
    /// <summary>内存环上限（仅保最新，完整历史在文件）。</summary>
    public const int MaxEntries = 200;

    /// <summary>日志页默认每页条数。</summary>
    public const int DefaultPageSize = 100;

    private static readonly object Gate = new();
    private static readonly List<AppLogEntry> Items = new();

    private static long _lastChangedMs;
    private static int _changedPending;
    /// <summary>当前运行中程序的最低写入级别；null 表示不过滤（宿主日志照常写）。</summary>
    private static AppLogLevel? _programMinWriteLevel;

    public static event Action? Changed;

    /// <summary>
    /// 程序运行期间的最低落盘级别：Debug &lt; Info &lt; Warn &lt; Error。
    /// 低于此级别的条目直接丢弃；结束运行请 <see cref="ClearProgramMinWriteLevel"/>。
    /// </summary>
    public static void SetProgramMinWriteLevel(AppLogLevel minLevel)
        => _programMinWriteLevel = minLevel;

    public static void ClearProgramMinWriteLevel()
        => _programMinWriteLevel = null;

    public static AppLogLevel? ProgramMinWriteLevel => _programMinWriteLevel;

    /// <summary>是否达到当前程序最低写入级别（无绑定时一律允许）。</summary>
    public static bool ShouldWrite(AppLogLevel level)
    {
        var min = _programMinWriteLevel;
        if (min is null) return true;
        return Rank(level) >= Rank(min.Value);
    }

    /// <summary>严重度排序：数字越大越「重」，用于最低级别过滤。</summary>
    public static int Rank(AppLogLevel level) => level switch
    {
        AppLogLevel.Debug => 0,
        AppLogLevel.Info => 1,
        AppLogLevel.Warn => 2,
        AppLogLevel.Error => 3,
        _ => 1
    };

    public static void Debug(string message, string? programId = null, string? programName = null)
        => Add(AppLogLevel.Debug, message, programId, programName);
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

    /// <summary>内存环快照（仅最新 <see cref="MaxEntries"/> 条）。分页请用 <see cref="GetPage"/>。</summary>
    public static IReadOnlyList<AppLogEntry> Snapshot(
        AppLogLevelFilter level = AppLogLevelFilter.All,
        string? programId = null,
        string? programName = null)
    {
        lock (Gate)
        {
            IEnumerable<AppLogEntry> query = Items;
            if (level != AppLogLevelFilter.All)
                query = query.Where(e => (int)e.Level == (int)level);
            if (!string.IsNullOrEmpty(programId) || !string.IsNullOrEmpty(programName))
                query = query.Where(e => BelongsToProgramPublic(e, programId, programName));
            return query.ToArray();
        }
    }

    /// <summary>兼容旧调用：仅错误 ⇔ <see cref="AppLogLevelFilter.Error"/>。</summary>
    public static IReadOnlyList<AppLogEntry> Snapshot(
        bool errorsOnly,
        string? programId = null,
        string? programName = null)
        => Snapshot(errorsOnly ? AppLogLevelFilter.Error : AppLogLevelFilter.All, programId, programName);

    /// <summary>
    /// 分页查询：<paramref name="pageFromLatest"/>=0 为最新一页（默认）。
    /// 读磁盘历史，供翻页；最新页实时追加请用内存 <see cref="Snapshot"/> + <see cref="SyncTo"/>。
    /// </summary>
    public static AppLogPage GetPage(
        int pageFromLatest = 0,
        int pageSize = DefaultPageSize,
        AppLogLevelFilter level = AppLogLevelFilter.All,
        string? programId = null,
        string? programName = null)
    {
        var page = AppLogFileStore.QueryPage(
            pageFromLatest, pageSize, level, programId, programName, memoryTail: null);
        if (page.TotalCount > 0)
            return page;

        var mem = Snapshot(level, programId, programName);
        pageSize = Math.Clamp(pageSize, 20, 500);
        pageFromLatest = Math.Max(0, pageFromLatest);
        var total = mem.Count;
        var totalPages = total == 0 ? 0 : (total + pageSize - 1) / pageSize;
        if (total == 0)
        {
            return new AppLogPage
            {
                Items = Array.Empty<AppLogEntry>(),
                PageFromLatest = 0,
                PageSize = pageSize,
                TotalCount = 0,
                TotalPages = 0
            };
        }
        pageFromLatest = Math.Min(pageFromLatest, totalPages - 1);
        var end = total - pageFromLatest * pageSize;
        var start = Math.Max(0, end - pageSize);
        return new AppLogPage
        {
            Items = mem.Skip(start).Take(end - start).ToArray(),
            PageFromLatest = pageFromLatest,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = totalPages
        };
    }

    public static AppLogPage GetPage(
        int pageFromLatest,
        int pageSize,
        bool errorsOnly,
        string? programId,
        string? programName)
        => GetPage(
            pageFromLatest,
            pageSize,
            errorsOnly ? AppLogLevelFilter.Error : AppLogLevelFilter.All,
            programId,
            programName);

    public static string LogsDirectory => AppLogFileStore.DirectoryPath;

    internal static bool BelongsToProgramPublic(AppLogEntry entry, string? programId, string? programName)
        => BelongsToProgram(entry, programId, programName);

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

    /// <summary>清空内存环并删除全部本地日志文件。</summary>
    public static void Clear()
    {
        lock (Gate)
            Items.Clear();
        AppLogFileStore.ClearAll();
        RaiseChanged();
    }

    /// <summary>导出筛选条件下的日志（读近 3 天文件；无文件时回退内存；过大时截断）。</summary>
    public static string FormatExport(
        AppLogLevelFilter level = AppLogLevelFilter.All,
        string? programId = null,
        string? programName = null)
    {
        var fromFiles = AppLogFileStore.FormatExportFromFiles(level, programId, programName);
        if (!string.IsNullOrEmpty(fromFiles))
            return fromFiles;

        var items = Snapshot(level, programId, programName);
        if (items.Count == 0) return "";

        var sb = new StringBuilder(items.Count * 96);
        sb.AppendLine($"# FlyScreen log export {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# entries={items.Count} level={level} source=memory");
        sb.AppendLine();
        foreach (var e in items)
        {
            var tag = e.Level switch
            {
                AppLogLevel.Error => "错误",
                AppLogLevel.Warn => "警告",
                _ => "信息"
            };
            sb.Append('[').Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
              .Append(tag).Append("  ").AppendLine(e.Message);
        }
        return sb.ToString();
    }

    public static string FormatExport(
        bool errorsOnly,
        string? programId,
        string? programName)
        => FormatExport(
            errorsOnly ? AppLogLevelFilter.Error : AppLogLevelFilter.All,
            programId,
            programName);

    public static string SuggestExportFileName()
        => $"FlyScreen-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt";

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

    /// <summary>
    /// 相对水印的增量。失配时 <see cref="AppLogDelta.Reset"/>=true，UI 应整页替换尾部，
    /// 禁止按时间重放整表（那是闪烁回顶的根因）。
    /// </summary>
    public static AppLogDelta DeltaSince(
        AppLogEntry? watermark,
        AppLogLevelFilter level = AppLogLevelFilter.All,
        string? programId = null,
        string? programName = null,
        int pageSize = DefaultPageSize)
    {
        pageSize = Math.Clamp(pageSize, 20, 500);
        lock (Gate)
        {
            var filtered = new List<AppLogEntry>(Items.Count);
            foreach (var e in Items)
            {
                if (level != AppLogLevelFilter.All && (int)e.Level != (int)level)
                    continue;
                if (!string.IsNullOrEmpty(programId) || !string.IsNullOrEmpty(programName))
                {
                    if (!BelongsToProgram(e, programId, programName))
                        continue;
                }
                filtered.Add(e);
            }

            if (filtered.Count == 0)
                return new AppLogDelta(Array.Empty<AppLogEntry>(), reset: true);

            if (watermark == null)
                return new AppLogDelta(TakeLast(filtered, pageSize), reset: true);

            for (var i = filtered.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(filtered[i], watermark))
                    continue;
                if (i + 1 >= filtered.Count)
                    return new AppLogDelta(Array.Empty<AppLogEntry>(), reset: false);
                var n = filtered.Count - (i + 1);
                var tail = new AppLogEntry[n];
                filtered.CopyTo(i + 1, tail, 0, n);
                return new AppLogDelta(tail, reset: false);
            }

            // 水印已被环缓冲丢掉：只重置为最新一页，绝不按 Time 重放
            return new AppLogDelta(TakeLast(filtered, pageSize), reset: true);
        }
    }

    /// <summary>列表增量追加（不从头部 RemoveAt，避免 ObservableCollection 触发整表重绘）。</summary>
    public static bool SyncTo(
        ObservableCollection<AppLogEntry> ui,
        IReadOnlyList<AppLogEntry> snapshot)
    {
        AppLogEntry? last = ui.Count > 0 ? ui[^1] : null;
        var start = FindAppendStart(snapshot, last);
        // start==0 且已有 last：视为失配，禁止整表重放
        if (last != null && start == 0)
            return false;
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
        if (last == null)
            return snapshot.Count == 0 ? 0 : Math.Max(0, snapshot.Count - DefaultPageSize);

        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(snapshot[i], last))
                return i + 1;
        }

        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            if (SameEntry(snapshot[i], last))
                return i + 1;
        }

        // 失配：不重放（旧逻辑按 Time>last 会从 0 灌入整表 → 闪烁）
        return snapshot.Count;
    }

    private static AppLogEntry[] TakeLast(List<AppLogEntry> list, int count)
    {
        if (list.Count <= count)
            return list.ToArray();
        var start = list.Count - count;
        var arr = new AppLogEntry[count];
        list.CopyTo(start, arr, 0, count);
        return arr;
    }

    private static bool SameEntry(AppLogEntry a, AppLogEntry b)
        => a.Time == b.Time && a.Level == b.Level && a.Message == b.Message;

    private static void Add(AppLogLevel level, string message, string? programId = null, string? programName = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        // 程序运行中的最低级别：仅过滤带 programId 的脚本输出，避免误伤宿主日志
        if (!string.IsNullOrWhiteSpace(programId) && !ShouldWrite(level))
            return;
        var entry = new AppLogEntry
        {
            Time = DateTime.Now,
            Level = level,
            Message = message.Trim(),
            ProgramId = string.IsNullOrWhiteSpace(programId) ? null : programId,
            ProgramName = string.IsNullOrWhiteSpace(programName) ? null : programName
        };

        lock (Gate)
        {
            Items.Add(entry);
            if (Items.Count > MaxEntries)
                Items.RemoveRange(0, Items.Count - MaxEntries);
        }

        AppLogFileStore.Append(entry);
        RaiseChanged();
    }

    /// <summary>合并/节流 UI 通知，避免高频写日志时界面卡死。</summary>
    private static void RaiseChanged()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastChangedMs);
        if (now - last >= 250)
        {
            Interlocked.Exchange(ref _lastChangedMs, now);
            try { Changed?.Invoke(); } catch { /* ignore */ }
            return;
        }

        if (Interlocked.Exchange(ref _changedPending, 1) == 1)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Thread.Sleep(250);
                Interlocked.Exchange(ref _changedPending, 0);
                Interlocked.Exchange(ref _lastChangedMs, Environment.TickCount64);
                try { Changed?.Invoke(); } catch { /* ignore */ }
            }
            catch
            {
                Interlocked.Exchange(ref _changedPending, 0);
            }
        });
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
