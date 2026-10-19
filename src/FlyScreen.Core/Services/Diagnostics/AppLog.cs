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

    public static void Info(string message) => Add(AppLogLevel.Info, message);
    public static void Warn(string message) => Add(AppLogLevel.Warn, message);
    public static void Error(string message) => Add(AppLogLevel.Error, message);
    public static void Error(Exception ex, string? context = null)
    {
        var msg = string.IsNullOrWhiteSpace(context)
            ? ex.Message
            : $"{context}: {ex.Message}";
        Add(AppLogLevel.Error, msg);
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

    public static IReadOnlyList<AppLogEntry> Snapshot(bool errorsOnly = false)
    {
        lock (Gate)
        {
            if (!errorsOnly)
                return Items.ToArray();
            return Items.Where(e => e.Level == AppLogLevel.Error).ToArray();
        }
    }

    public static void Clear()
    {
        lock (Gate)
            Items.Clear();
        try { Changed?.Invoke(); } catch { /* ignore */ }
    }

    private static void Add(AppLogLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (Gate)
        {
            Items.Add(new AppLogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Message = message.Trim()
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
