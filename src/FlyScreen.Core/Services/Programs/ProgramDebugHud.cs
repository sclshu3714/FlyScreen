namespace FlyScreen.Services.Programs;

/// <summary>
/// 程序运行期调试浮层：由手机宿主注册实现；开启 settings.debugLog 后左中显示最近若干行日志。
/// </summary>
public static class ProgramDebugHud
{
    public const int MaxLines = 5;

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
                var line = $"[{e.Time:HH:mm:ss}] {msg}";
                if (line.Length > 72)
                    line = line[..71] + "…";
                lines.Add(line);
            }
            if (lines.Count == 0)
                lines.Add($"[{DateTime.Now:HH:mm:ss}] 调试日志已开启");
            _setLines?.Invoke(lines);
        }
        catch { /* ignore */ }
    }
}
