namespace FlyScreen.Services.Programs;

/// <summary>
/// 程序运行期调试浮层：由手机宿主注册实现；仅 <c>debugLog=true</c> 时显示（与 logLevel 无关）。
/// </summary>
public static class ProgramDebugHud
{
    /// <summary>浮层滚动区内保留的最近日志行数。</summary>
    public const int MaxLines = 80;

    private static readonly object Gate = new();
    private static Action<bool, string?, string?>? _setActive;
    private static Action<IReadOnlyList<string>>? _setLines;
    private static bool _active;
    private static string? _programId;
    private static string? _programName;
    private static bool _subscribed;

    /// <summary>MAUI Android 等在启动时注册。</summary>
    public static void Register(
        Action<bool, string?, string?> setActive,
        Action<IReadOnlyList<string>> setLines)
    {
        lock (Gate)
        {
            _setActive = setActive;
            _setLines = setLines;
        }
    }

    public static void Start(string programId, string? programName)
    {
        lock (Gate)
        {
            _active = true;
            _programId = programId;
            _programName = programName;
            EnsureSubscribed();
        }
        try { _setActive?.Invoke(true, programId, programName); } catch { /* ignore */ }
        PushSnapshot();
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _active = false;
            _programId = null;
            _programName = null;
        }
        try { _setActive?.Invoke(false, null, null); } catch { /* ignore */ }
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed) return;
        _subscribed = true;
        AppLog.Changed += OnAppLogChanged;
    }

    private static void OnAppLogChanged() => PushSnapshot();

    private static void PushSnapshot()
    {
        string? id;
        string? name;
        lock (Gate)
        {
            if (!_active) return;
            id = _programId;
            name = _programName;
        }

        try
        {
            var snap = AppLog.Snapshot(AppLogLevelFilter.All, id, name);
            var lines = new List<string>(MaxLines);
            var start = Math.Max(0, snap.Count - MaxLines);
            for (var i = start; i < snap.Count; i++)
            {
                var e = snap[i];
                var msg = e.Message ?? "";
                if (!string.IsNullOrEmpty(name))
                {
                    var prefix = "[" + name + "] ";
                    if (msg.StartsWith(prefix, StringComparison.Ordinal))
                        msg = msg[prefix.Length..];
                }
                var tag = e.Level switch
                {
                    AppLogLevel.Error => "E",
                    AppLogLevel.Warn => "W",
                    AppLogLevel.Debug => "D",
                    _ => "I"
                };
                var line = $"[{e.Time:HH:mm:ss}] {tag} {msg}";
                if (line.Length > 240)
                    line = line[..239] + "…";
                lines.Add(line);
            }
            if (lines.Count == 0)
                lines.Add($"[{DateTime.Now:HH:mm:ss}] I 调试浮层已开启（可拖标题栏移动，右下角缩放）");
            _setLines?.Invoke(lines);
        }
        catch { /* ignore */ }
    }
}
