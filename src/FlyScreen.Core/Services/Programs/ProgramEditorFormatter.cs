using System.Text;

namespace FlyScreen.Services.Programs;

/// <summary>各语言源码基础自动格式化（缩进 / 空白 / 换行），供各端编辑器统一调用。</summary>
public static class ProgramEditorFormatter
{
    private const int IndentSize = 4;

    public static string Format(string? text, ProgramLanguage lang)
    {
        var src = ProgramCatalog.NormalizeSourceText(text);
        if (string.IsNullOrEmpty(src)) return "";

        src = NormalizeBasicWhitespace(src);
        return lang switch
        {
            ProgramLanguage.Python => FormatPython(src),
            ProgramLanguage.CSharp => FormatBraceLanguage(src, sharpStyle: true),
            ProgramLanguage.JavaScript => FormatBraceLanguage(src, sharpStyle: false),
            ProgramLanguage.PowerShell => FormatBraceLanguage(src, sharpStyle: false),
            ProgramLanguage.Bash => FormatBash(src),
            ProgramLanguage.Batch => FormatBatch(src),
            _ => src
        };
    }

    /// <summary>仅规范化换行与行尾空白、Tab→空格，不做缩进重排。</summary>
    public static string NormalizeBasicWhitespace(string text)
    {
        text = ProgramCatalog.NormalizeSourceText(text).Replace("\t", new string(' ', IndentSize));
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();

        // 连续空行最多保留 1 个
        var sb = new StringBuilder(text.Length);
        var blank = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                blank++;
                if (blank > 1) continue;
                sb.Append('\n');
            }
            else
            {
                blank = 0;
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n') + (text.EndsWith('\n') ? "\n" : "");
    }

    /// <summary>
    /// Python 缩进敏感：不可用 ":" 启发式重排（会把顶格 if __name__ 缩进进函数体）。
    /// 仅把原有相对缩进归一成 4 空格倍数，并修复误缩进的 if __name__ 入口。
    /// </summary>
    private static string FormatPython(string text)
        => RepairPythonMainGuard(NormalizeIndentLevels(text));

    /// <summary>若 if __name__ == "__main__" 被错误缩进进函数体，整段拉回顶格。</summary>
    private static string RepairPythonMainGuard(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("if __name__", StringComparison.Ordinal)) continue;
            var bad = LeadingWidth(lines[i]);
            if (bad <= 0) continue;

            for (var j = i; j < lines.Length; j++)
            {
                if (string.IsNullOrWhiteSpace(lines[j])) continue;
                var w = LeadingWidth(lines[j]);
                if (j > i && w < bad) break;
                if (w >= bad)
                    lines[j] = new string(' ', w - bad) + lines[j].TrimStart();
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>按各行原有前导空白相对层级，输出统一 4 空格缩进。</summary>
    private static string NormalizeIndentLevels(string text)
    {
        var raw = text.Split('\n');
        var widths = new int[raw.Length];
        var distinct = new SortedSet<int>();
        for (var i = 0; i < raw.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(raw[i]))
            {
                widths[i] = -1;
                continue;
            }
            widths[i] = LeadingWidth(raw[i]);
            distinct.Add(widths[i]);
        }

        // 宽度 → 层级（0,1,2…）；保持相对顺序
        var rank = new Dictionary<int, int>();
        var level = 0;
        foreach (var w in distinct)
            rank[w] = level++;

        var sb = new StringBuilder(text.Length + 64);
        for (var i = 0; i < raw.Length; i++)
        {
            if (widths[i] < 0)
            {
                if (i < raw.Length - 1) sb.Append('\n');
                continue;
            }
            sb.Append(' ', rank[widths[i]] * IndentSize);
            sb.Append(raw[i].Trim());
            if (i < raw.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static int LeadingWidth(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += IndentSize;
            else break;
        }
        return n;
    }

    private static string FormatBraceLanguage(string text, bool sharpStyle)
    {
        var raw = text.Split('\n');
        var sb = new StringBuilder(text.Length + 64);
        var indent = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var trimmed = raw[i].Trim();
            if (trimmed.Length == 0)
            {
                if (i < raw.Length - 1) sb.Append('\n');
                continue;
            }

            var open = CountCharOutsideString(trimmed, '{');
            var close = CountCharOutsideString(trimmed, '}');
            // 行首的 } 先减缩进
            var leadingClose = 0;
            foreach (var c in trimmed)
            {
                if (c == '}') leadingClose++;
                else if (!char.IsWhiteSpace(c)) break;
            }
            var lineIndent = Math.Max(0, indent - leadingClose);

            sb.Append(' ', lineIndent * IndentSize);
            if (sharpStyle)
                sb.Append(NormalizeCsharpSpacing(trimmed));
            else
                sb.Append(NormalizeJsSpacing(trimmed));
            if (i < raw.Length - 1) sb.Append('\n');

            indent = Math.Max(0, indent + open - close);
        }
        return sb.ToString();
    }

    private static string NormalizeCsharpSpacing(string line)
    {
        if (line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith('*'))
            return line;
        // 简单：} else / ) { 等关键词前后补空格已在书写中；此处压缩多余空格（跳过字符串）
        return CollapseSpacesOutsideString(line);
    }

    private static string NormalizeJsSpacing(string line)
        => CollapseSpacesOutsideString(line);

    private static string FormatBash(string text)
    {
        var raw = text.Split('\n');
        var sb = new StringBuilder(text.Length + 64);
        var indent = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var trimmed = raw[i].Trim();
            if (trimmed.Length == 0)
            {
                if (i < raw.Length - 1) sb.Append('\n');
                continue;
            }

            if (IsBashDedent(trimmed))
                indent = Math.Max(0, indent - 1);

            sb.Append(' ', indent * IndentSize);
            sb.Append(trimmed);
            if (i < raw.Length - 1) sb.Append('\n');

            if (IsBashIndent(trimmed))
                indent++;
        }
        return sb.ToString();
    }

    private static bool IsBashIndent(string t)
        => t is "then" or "do" or "else" or "{"
           || t.EndsWith(" then", StringComparison.Ordinal)
           || t.EndsWith(" do", StringComparison.Ordinal)
           || (t.StartsWith("function ", StringComparison.Ordinal) && t.EndsWith('{'));

    private static bool IsBashDedent(string t)
        => t is "fi" or "done" or "else" or "elif" or "esac" or "}"
           || t.StartsWith("elif ", StringComparison.Ordinal)
           || t.StartsWith("else", StringComparison.Ordinal);

    private static string FormatBatch(string text)
    {
        var raw = text.Split('\n');
        var sb = new StringBuilder(text.Length + 64);
        for (var i = 0; i < raw.Length; i++)
        {
            var trimmed = raw[i].Trim();
            // Batch 标签/指令通常顶格；块内用缩进的简单启发式：以 ) 开头的减一级
            var indent = 0;
            if (trimmed.StartsWith(')'))
                indent = 0;
            else if (i > 0 && LooksLikeBatchBlock(raw, i))
                indent = 1;

            if (trimmed.Length == 0)
            {
                if (i < raw.Length - 1) sb.Append('\n');
                continue;
            }

            sb.Append(' ', indent * IndentSize);
            sb.Append(trimmed);
            if (i < raw.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool LooksLikeBatchBlock(string[] lines, int index)
    {
        for (var j = index - 1; j >= 0; j--)
        {
            var t = lines[j].Trim();
            if (t.Length == 0) continue;
            if (t.EndsWith('(')) return true;
            if (t.StartsWith(')')) return false;
            break;
        }
        return false;
    }

    private static int CountCharOutsideString(string line, char target)
    {
        var n = 0;
        var inS = false;
        var inD = false;
        var escape = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && (inS || inD)) { escape = true; continue; }
            if (!inD && c == '\'') { inS = !inS; continue; }
            if (!inS && c == '"') { inD = !inD; continue; }
            if (!inS && !inD && c == target) n++;
        }
        return n;
    }

    private static string CollapseSpacesOutsideString(string line)
    {
        var sb = new StringBuilder(line.Length);
        var inS = false;
        var inD = false;
        var escape = false;
        var prevSpace = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (escape) { sb.Append(c); escape = false; prevSpace = false; continue; }
            if (c == '\\' && (inS || inD)) { sb.Append(c); escape = true; continue; }
            if (!inD && c == '\'') { inS = !inS; sb.Append(c); prevSpace = false; continue; }
            if (!inS && c == '"') { inD = !inD; sb.Append(c); prevSpace = false; continue; }
            if (!inS && !inD && char.IsWhiteSpace(c))
            {
                if (prevSpace) continue;
                sb.Append(' ');
                prevSpace = true;
                continue;
            }
            sb.Append(c);
            prevSpace = false;
        }
        return sb.ToString().TrimEnd();
    }
}
