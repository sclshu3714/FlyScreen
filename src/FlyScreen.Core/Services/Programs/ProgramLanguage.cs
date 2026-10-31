namespace FlyScreen.Services.Programs;

/// <summary>可单文件执行的程序语言（预留扩展）。</summary>
public enum ProgramLanguage
{
    Python = 0,
    CSharp = 1,
    JavaScript = 2,
    PowerShell = 3,
    Bash = 4,
    Batch = 5
}

public static class ProgramLanguageInfo
{
    public static string DisplayName(ProgramLanguage lang) => lang switch
    {
        ProgramLanguage.Python => "Python",
        ProgramLanguage.CSharp => "C# (.NET)",
        ProgramLanguage.JavaScript => "JavaScript",
        ProgramLanguage.PowerShell => "PowerShell",
        ProgramLanguage.Bash => "Bash",
        ProgramLanguage.Batch => "Batch",
        _ => lang.ToString()
    };

    public static string Extension(ProgramLanguage lang) => lang switch
    {
        ProgramLanguage.Python => ".py",
        ProgramLanguage.CSharp => ".cs",
        ProgramLanguage.JavaScript => ".js",
        ProgramLanguage.PowerShell => ".ps1",
        ProgramLanguage.Bash => ".sh",
        ProgramLanguage.Batch => ".bat",
        _ => ".txt"
    };

    public static string DefaultFileName(ProgramLanguage lang)
        => "main" + Extension(lang);

    public static IReadOnlyList<ProgramLanguage> All { get; } =
    [
        ProgramLanguage.Python,
        ProgramLanguage.CSharp,
        ProgramLanguage.JavaScript,
        ProgramLanguage.PowerShell,
        ProgramLanguage.Bash,
        ProgramLanguage.Batch
    ];

    /// <summary>手机端可本机运行的语言（嵌入引擎）。</summary>
    public static IReadOnlyList<ProgramLanguage> MobileRunnable { get; } =
    [
        ProgramLanguage.JavaScript,
        ProgramLanguage.CSharp
    ];

    public static IReadOnlyList<ProgramLanguage> ForCurrentPlatform
        => InProcessProgramExecutor.IsMobileHost ? MobileRunnable : All;

    public static bool TryParse(string? text, out ProgramLanguage lang)
    {
        if (Enum.TryParse(text, ignoreCase: true, out lang))
            return true;
        if (string.Equals(text, "C#", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "csharp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "cs", StringComparison.OrdinalIgnoreCase))
        {
            lang = ProgramLanguage.CSharp;
            return true;
        }
        if (string.Equals(text, "js", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "node", StringComparison.OrdinalIgnoreCase))
        {
            lang = ProgramLanguage.JavaScript;
            return true;
        }
        if (string.Equals(text, "py", StringComparison.OrdinalIgnoreCase))
        {
            lang = ProgramLanguage.Python;
            return true;
        }
        lang = ProgramLanguage.Python;
        return false;
    }
}
