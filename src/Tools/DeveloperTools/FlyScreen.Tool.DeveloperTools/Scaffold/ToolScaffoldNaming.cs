using System.Text;

namespace FlyScreen.Tool.DeveloperTools.Scaffold;

public static class ToolScaffoldNaming
{
    public static string NormalizeProjectName(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        // 允许已是 PascalCase / 带空格 / kebab
        s = s.Replace('-', ' ').Replace('_', ' ');
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            var token = new string(p.Where(char.IsLetterOrDigit).ToArray());
            if (token.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(token[0]));
            if (token.Length > 1)
                sb.Append(token[1..]);
        }
        var name = sb.ToString();
        if (name.Length == 0) return "";
        if (char.IsDigit(name[0]))
            name = "Tool" + name;
        return name;
    }

    public static string ToToolId(string projectNameEn)
    {
        var name = NormalizeProjectName(projectNameEn);
        if (name.Length == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static ToolScaffoldIdentity BuildIdentity(string projectNameEn)
    {
        var folder = NormalizeProjectName(projectNameEn);
        var toolId = ToToolId(folder);
        var asm = "FlyScreen.Tool." + folder;
        return new ToolScaffoldIdentity
        {
            ToolId = toolId,
            FolderName = folder,
            AssemblyName = asm,
            Namespace = asm,
            LocPrefix = "Tool." + folder,
            IconKey = "tool-" + toolId,
            NameKey = "Tool." + folder + ".Name",
            SummaryKey = "Tool." + folder + ".Summary"
        };
    }

    public static string? Validate(ToolScaffoldRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DisplayNameZh))
            return "请填写中文显示名称";
        var en = NormalizeProjectName(req.ProjectNameEn);
        if (en.Length == 0)
            return "请填写英文项目名称（字母数字，如 SampleTool）";
        if (!req.SupportDesktop && !req.SupportPhone)
            return "请至少勾选一个支持端（电脑 / 手机）";
        if (string.IsNullOrWhiteSpace(req.OutputRoot))
            return "请选择项目生成位置";
        try
        {
            _ = Path.GetFullPath(req.OutputRoot.Trim());
        }
        catch
        {
            return "生成位置路径无效";
        }
        if (req.UiMode == ToolUiMode.Workspace && !req.SupportDesktop && !req.SupportPhone)
            return "嵌入工作区需要至少一个 UI 端";
        return null;
    }

    public static string EscapeCs(string s)
        => (s ?? "")
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    public static string EscapeJson(string s)
        => (s ?? "")
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
