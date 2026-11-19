namespace FlyScreen.Services.Programs;

/// <summary>编辑器文件切换列表左侧类型徽标（对齐 Cursor 风格）。</summary>
public static class ProgramEditorFileBadge
{
    public static string ForPath(string? relativePath)
    {
        var ext = Path.GetExtension(relativePath ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cs" => "C#",
            "js" or "mjs" or "cjs" => "JS",
            "ts" => "TS",
            "json" => "{}",
            "py" => "Py",
            "ps1" => "PS",
            "sh" or "bash" => "Sh",
            "bat" or "cmd" => "Bat",
            "rb" => "Rb",
            "go" => "Go",
            "rs" => "Rs",
            "java" => "Jv",
            "kt" => "Kt",
            "swift" => "Sw",
            "xml" or "axaml" or "xaml" => "XML",
            "md" => "Md",
            "txt" => "Txt",
            "dll" => "DLL",
            _ => string.IsNullOrEmpty(ext) ? "·" : ext.ToUpperInvariant()[..Math.Min(3, ext.Length)]
        };
    }

    public static string DisplayName(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "";
        return relativePath.Replace('\\', '/');
    }
}
