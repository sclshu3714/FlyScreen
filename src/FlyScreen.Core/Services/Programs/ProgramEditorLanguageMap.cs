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
}
