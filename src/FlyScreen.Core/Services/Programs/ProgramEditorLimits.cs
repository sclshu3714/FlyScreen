using FlyScreen.Helpers.Localization;

namespace FlyScreen.Services.Programs;

/// <summary>程序编辑器行数上限（小任务；过大易拖垮进程）。</summary>
public static class ProgramEditorLimits
{
    public const int MaxLines = 1000;

    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var n = 1;
        foreach (var c in text)
        {
            if (c == '\n') n++;
        }
        return n;
    }

    public static bool ExceedsLimit(string? text, out int lineCount)
    {
        lineCount = CountLines(text);
        return lineCount > MaxLines;
    }

    public static bool IsWithinLimit(string? text)
        => !ExceedsLimit(text, out _);

    public static string ExceedMessage(int lineCount)
        => Loc.T("Prog.LineLimitExceed", MaxLines, lineCount);

    public static string StatusFragment(int currentLine, string? source)
    {
        var total = CountLines(source);
        var over = total > MaxLines;
        var lines = Loc.T("Prog.LineLimitStatus", currentLine, total, MaxLines);
        return over ? lines + "  ·  " + Loc.T("Prog.LineLimitBlocked") : lines;
    }
}
