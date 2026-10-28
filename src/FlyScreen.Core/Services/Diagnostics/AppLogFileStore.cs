using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FlyScreen.Services;

/// <summary>
/// 日志落盘：按天 + 单文件 5MB 分块；超过 3 天的文件自动删除。
/// 行格式：<c>[yyyy-MM-dd HH:mm:ss.fff] Level progId=… progName=… | message</c>
/// </summary>
internal static class AppLogFileStore
{
    public const long MaxFileBytes = 5L * 1024 * 1024;
    public const int RetainDays = 3;

    private static readonly object Gate = new();
    private static readonly Regex LineRe = new(
        @"^\[(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]\s+(?<lv>\w+)\s+progId=(?<pid>[^\s]*)\s+progName=(?<pn>.*?)\s+\|\s(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static StreamWriter? _writer;
    private static string? _writerPath;
    private static DateOnly _writerDay;
    private static int _writerPart;
    private static long _writerBytes;
    private static DateOnly _lastCleanupDay;

    public static string DirectoryPath => FlyScreenDataPaths.LogsDirectory;

    public static void Append(AppLogEntry entry)
    {
        try
        {
            lock (Gate)
            {
                EnsureWriter(entry.Time);
                var line = FormatLine(entry);
                _writer!.WriteLine(line);
                _writer.Flush();
                _writerBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_writerBytes >= MaxFileBytes)
                    RollPart(entry.Time);
            }
        }
        catch
        {
            /* 落盘失败不影响主流程 */
        }
    }

    /// <summary>关闭写入并删除全部日志文件（清除日志用）。</summary>
    public static void ClearAll()
    {
        lock (Gate)
        {
            CloseWriter_NoLock();
            _writerDay = default;
            _writerPart = 0;
            _writerBytes = 0;
        }

        try
        {
            var dir = DirectoryPath;
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir, "flyscreen-*.log"))
            {
                try { File.Delete(path); } catch { /* ignore locked/in-use */ }
            }
        }
        catch
        {
            /* ignore */
        }
    }

    public static void CleanupOldFiles()
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            lock (Gate)
            {
                if (_lastCleanupDay == today) return;
                _lastCleanupDay = today;
            }

            var dir = DirectoryPath;
            if (!Directory.Exists(dir)) return;
            var cutoff = today.AddDays(-RetainDays);
            foreach (var path in Directory.EnumerateFiles(dir, "flyscreen-*.log"))
            {
                var name = Path.GetFileName(path);
                if (!TryParseFileDate(name, out var day)) continue;
                if (day < cutoff)
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>
    /// 从文件（及可选内存尾部）取「距最新」第 <paramref name="pageFromLatest"/> 页。
    /// pageFromLatest=0 为最新一页。
    /// </summary>
    public static AppLogPage QueryPage(
        int pageFromLatest,
        int pageSize,
        AppLogLevelFilter level,
        string? programId,
        string? programName,
        IReadOnlyList<AppLogEntry>? memoryTail)
    {
        pageFromLatest = Math.Max(0, pageFromLatest);
        pageSize = Math.Clamp(pageSize, 20, 500);
        var need = (pageFromLatest + 1) * pageSize;
        var ring = new LinkedList<AppLogEntry>();
        var total = 0;

        void Push(AppLogEntry e)
        {
            if (!Matches(e, level, programId, programName)) return;
            total++;
            ring.AddLast(e);
            while (ring.Count > need)
                ring.RemoveFirst();
        }

        try
        {
            foreach (var path in EnumerateLogFilesOldestFirst())
            {
                foreach (var e in ReadFile(path))
                    Push(e);
            }
        }
        catch
        {
            /* 读盘失败时仍可用内存 */
        }

        if (memoryTail != null)
        {
            foreach (var e in memoryTail)
                Push(e);
        }

        var all = ring.ToList();
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

        pageFromLatest = Math.Min(pageFromLatest, Math.Max(0, totalPages - 1));
        var endExclusive = all.Count - pageFromLatest * pageSize;
        var start = Math.Max(0, endExclusive - pageSize);
        if (endExclusive <= 0)
            return new AppLogPage
            {
                Items = Array.Empty<AppLogEntry>(),
                PageFromLatest = pageFromLatest,
                PageSize = pageSize,
                TotalCount = total,
                TotalPages = totalPages
            };

        var slice = all.GetRange(start, endExclusive - start);
        return new AppLogPage
        {
            Items = slice,
            PageFromLatest = pageFromLatest,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = totalPages
        };
    }

    public static string FormatExportFromFiles(
        AppLogLevelFilter level,
        string? programId,
        string? programName,
        int maxChars = 8_000_000)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine($"# FlyScreen log export {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# source=files level={level} retainDays={RetainDays}");
        sb.AppendLine();
        var count = 0;
        var truncated = false;
        try
        {
            foreach (var path in EnumerateLogFilesOldestFirst())
            {
                foreach (var e in ReadFile(path))
                {
                    if (!Matches(e, level, programId, programName)) continue;
                    AppendExportLine(sb, e);
                    count++;
                    if (sb.Length >= maxChars)
                    {
                        truncated = true;
                        break;
                    }
                }
                if (truncated) break;
            }
        }
        catch
        {
            /* ignore */
        }

        if (truncated)
            sb.AppendLine("# …truncated…");
        sb.AppendLine($"# exported={count}");
        return count == 0 ? "" : sb.ToString();
    }

    private static void AppendExportLine(StringBuilder sb, AppLogEntry e)
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

    private static void EnsureWriter(DateTime time)
    {
        var day = DateOnly.FromDateTime(time);
        if (_writer != null && _writerDay == day && _writerBytes < MaxFileBytes)
            return;

        CloseWriter_NoLock();
        Directory.CreateDirectory(DirectoryPath);
        _writerDay = day;
        _writerPart = FindNextPart(day);
        _writerPath = PartPath(day, _writerPart);
        var exists = File.Exists(_writerPath);
        _writerBytes = exists ? new FileInfo(_writerPath).Length : 0;
        if (_writerBytes >= MaxFileBytes)
        {
            _writerPart++;
            _writerPath = PartPath(day, _writerPart);
            _writerBytes = 0;
            exists = false;
        }
        _writer = new StreamWriter(
            new FileStream(
                _writerPath,
                exists ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };
        CleanupOldFiles();
    }

    private static void RollPart(DateTime time)
    {
        CloseWriter_NoLock();
        _writerDay = DateOnly.FromDateTime(time);
        _writerPart++;
        _writerPath = PartPath(_writerDay, _writerPart);
        _writerBytes = 0;
        _writer = new StreamWriter(
            new FileStream(_writerPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };
    }

    private static void CloseWriter_NoLock()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _writerPath = null;
    }

    private static int FindNextPart(DateOnly day)
    {
        var max = 0;
        var prefix = $"flyscreen-{day:yyyyMMdd}";
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, prefix + "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == prefix.Length)
            {
                max = Math.Max(max, 1);
                continue;
            }
            if (name.Length > prefix.Length + 1 && name[prefix.Length] == '-'
                && int.TryParse(name[(prefix.Length + 1)..], out var n))
                max = Math.Max(max, n);
        }

        if (max == 0) return 1;
        var current = PartPath(day, max);
        try
        {
            if (File.Exists(current) && new FileInfo(current).Length >= MaxFileBytes)
                return max + 1;
        }
        catch { /* ignore */ }
        return max;
    }

    private static string PartPath(DateOnly day, int part)
        => part <= 1
            ? Path.Combine(DirectoryPath, $"flyscreen-{day:yyyyMMdd}.log")
            : Path.Combine(DirectoryPath, $"flyscreen-{day:yyyyMMdd}-{part}.log");

    private static IEnumerable<string> EnumerateLogFilesOldestFirst()
    {
        var dir = DirectoryPath;
        if (!Directory.Exists(dir))
            yield break;
        foreach (var path in Directory.EnumerateFiles(dir, "flyscreen-*.log").OrderBy(p => p, StringComparer.Ordinal))
            yield return path;
    }

    private static IEnumerable<AppLogEntry> ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (TryParseLine(line, out var e))
                yield return e;
        }
    }

    private static string FormatLine(AppLogEntry e)
    {
        var lv = e.Level switch
        {
            AppLogLevel.Error => "Error",
            AppLogLevel.Warn => "Warn",
            _ => "Info"
        };
        var pid = SanitizeToken(e.ProgramId);
        var pn = SanitizeToken(e.ProgramName);
        var msg = (e.Message ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return $"[{e.Time:yyyy-MM-dd HH:mm:ss.fff}] {lv} progId={pid} progName={pn} | {msg}";
    }

    private static string SanitizeToken(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static bool TryParseLine(string line, out AppLogEntry entry)
    {
        entry = null!;
        var m = LineRe.Match(line);
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(
                m.Groups["t"].Value,
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var t))
            return false;
        var lv = m.Groups["lv"].Value switch
        {
            "Error" => AppLogLevel.Error,
            "Warn" => AppLogLevel.Warn,
            _ => AppLogLevel.Info
        };
        var pid = m.Groups["pid"].Value;
        var pn = m.Groups["pn"].Value;
        entry = new AppLogEntry
        {
            Time = t,
            Level = lv,
            Message = m.Groups["msg"].Value,
            ProgramId = string.IsNullOrEmpty(pid) ? null : pid,
            ProgramName = string.IsNullOrEmpty(pn) ? null : pn
        };
        return true;
    }

    private static bool TryParseFileDate(string fileName, out DateOnly day)
    {
        day = default;
        // flyscreen-yyyyMMdd.log / flyscreen-yyyyMMdd-N.log
        if (!fileName.StartsWith("flyscreen-", StringComparison.OrdinalIgnoreCase))
            return false;
        var core = Path.GetFileNameWithoutExtension(fileName);
        if (core.Length < "flyscreen-yyyyMMdd".Length) return false;
        var datePart = core.Substring("flyscreen-".Length, 8);
        return DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    private static bool Matches(
        AppLogEntry e, AppLogLevelFilter level, string? programId, string? programName)
    {
        if (level != AppLogLevelFilter.All && (int)e.Level != (int)level)
            return false;
        if (string.IsNullOrEmpty(programId) && string.IsNullOrEmpty(programName))
            return true;
        return AppLog.BelongsToProgramPublic(e, programId, programName);
    }
}

public sealed class AppLogPage
{
    public required IReadOnlyList<AppLogEntry> Items { get; init; }
    /// <summary>0 = 最新一页。</summary>
    public int PageFromLatest { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    public bool HasOlder => PageFromLatest + 1 < TotalPages;
    public bool HasNewer => PageFromLatest > 0;
}
