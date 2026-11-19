namespace FlyScreen.Services.Programs;

/// <summary>程序语言 → 各端编辑器高亮标识。</summary>
public static class ProgramEditorLanguageMap
{
    /// <summary>AvalonEdit / AvaloniaEdit HighlightingManager 名称。</summary>
    public static string AvalonHighlightingName(ProgramLanguage lang) => lang switch
    {
        ProgramLanguage.Python => "Python",
        ProgramLanguage.CSharp => "C#",
        ProgramLanguage.JavaScript => "JavaScript",
        ProgramLanguage.PowerShell => "PowerShell",
        ProgramLanguage.Bash => "Bash",
        ProgramLanguage.Batch => "Batch",
        _ => "Text"
    };

    /// <summary>Monaco Editor language id。</summary>
    public static string MonacoLanguageId(ProgramLanguage lang) => lang switch
    {
        ProgramLanguage.Python => "python",
        ProgramLanguage.CSharp => "csharp",
        ProgramLanguage.JavaScript => "javascript",
        ProgramLanguage.PowerShell => "powershell",
        ProgramLanguage.Bash => "shell",
        ProgramLanguage.Batch => "bat",
        _ => "plaintext"
    };

    /// <summary>按相对路径扩展名解析高亮（如 fly-settings.json → JSON）。</summary>
    public static string AvalonHighlightingNameForPath(string? relativePath, ProgramLanguage fallback)
    {
        var ext = Path.GetExtension(relativePath ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "json" => "JavaScript", // Avalon 无独立 JSON 定义时用 JS
            "cs" => "C#",
            "js" or "mjs" or "cjs" => "JavaScript",
            "py" => "Python",
            "ps1" => "PowerShell",
            "sh" or "bash" => "Bash",
            "bat" or "cmd" => "Batch",
            _ => AvalonHighlightingName(fallback)
        };
    }

    public static string MonacoLanguageIdForPath(string? relativePath, ProgramLanguage fallback)
    {
        var ext = Path.GetExtension(relativePath ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "json" => "json",
            "cs" => "csharp",
            "js" or "mjs" or "cjs" => "javascript",
            "ts" => "typescript",
            "py" => "python",
            "ps1" => "powershell",
            "sh" or "bash" => "shell",
            "bat" or "cmd" => "bat",
            "xml" or "xaml" or "axaml" => "xml",
            "md" => "markdown",
            _ => MonacoLanguageId(fallback)
        };
    }
}
