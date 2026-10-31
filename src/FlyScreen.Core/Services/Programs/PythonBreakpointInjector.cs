namespace FlyScreen.Services.Programs;

/// <summary>Python 基础调试：在断点行前插入 breakpoint()。</summary>
public static class PythonBreakpointInjector
{
    public const string Marker = "breakpoint()  # flyscreen-bp";

    /// <summary>
    /// 返回注入后的源码；无有效断点时返回原文。
    /// </summary>
    public static string Inject(string source, IEnumerable<int> breakpoints)
    {
        var lines = (source ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count == 0) return source ?? "";

        var inject = breakpoints
            .Where(l => l >= 1 && l <= lines.Count)
            .Distinct()
            .OrderByDescending(l => l);

        var any = false;
        foreach (var line in inject)
        {
            var idx = line - 1;
            if (lines[idx].Contains("breakpoint()", StringComparison.Ordinal))
                continue;
            lines.Insert(idx, Marker);
            any = true;
        }

        return any ? string.Join("\n", lines) : (source ?? "");
    }

    public static int CountInjected(IEnumerable<int> breakpoints, string source)
    {
        var lines = (source ?? "").Replace("\r\n", "\n").Split('\n');
        return breakpoints.Where(l => l >= 1 && l <= lines.Length).Distinct().Count();
    }
}
