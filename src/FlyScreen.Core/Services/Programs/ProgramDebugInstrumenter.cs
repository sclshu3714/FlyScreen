using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlyScreen.Services.Programs;

/// <summary>
/// 用 Roslyn 按「语句」注入 LineAsync / PollAsync，避免插进数组初始化、多行表达式中间导致 CS1513。
/// </summary>
public static class ProgramDebugInstrumenter
{
    public static string Instrument(string source, IReadOnlyList<int> breakpoints)
    {
        _ = breakpoints; // 断点由 Gate.SetBreakpoints 运行时判断
        var raw = source ?? "";
        var tree = CSharpSyntaxTree.ParseText(
            raw,
            new CSharpParseOptions(
                languageVersion: LanguageVersion.Latest,
                kind: SourceCodeKind.Script));
        var root = tree.GetCompilationUnitRoot();

        // 从后往前插，避免偏移失效
        var edits = new List<(int Pos, string Text)>();

        foreach (var stmt in EnumerateInstrumentableStatements(root))
        {
            var line = stmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (line < 1) continue;

            // 插在「物理行首」，保留原语句缩进不被拆开
            var insertAt = GetLineStart(raw, stmt.SpanStart);
            var indent = GetIndentAt(raw, stmt.SpanStart);
            var locals = CollectLocalsBefore(root, stmt.SpanStart);
            var call = BuildLineCall(line, locals);
            edits.Add((insertAt, indent + call + "\n"));

            // while/for/foreach 循环体开头再插 Poll，便于运行中点「暂停」
            if (stmt is WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax)
                TryInsertPollInLoopBody(stmt, raw, edits);

            // 仅在「本语句就是 await Delay」后插 Poll（不要因内含 Delay 的 if/while 整块后插）
            if (stmt is ExpressionStatementSyntax && ContainsDelayAwait(stmt))
            {
                var after = stmt.Span.End; // 不含尾随 trivia，紧跟分号后
                edits.Add((after, "\n" + indent + "await __flyDbg.PollAsync();"));
            }
        }

        if (edits.Count == 0)
            return raw;

        // 同位置多次 Insert：先应用的最终更靠右。先 Line 后 Poll → 文本为 Poll / Line / 原语句
        edits.Sort((a, b) =>
        {
            var c = b.Pos.CompareTo(a.Pos);
            if (c != 0) return c;
            var aPoll = a.Text.Contains("PollAsync", StringComparison.Ordinal);
            var bPoll = b.Text.Contains("PollAsync", StringComparison.Ordinal);
            if (aPoll == bPoll) return 0;
            return aPoll ? 1 : -1; // Line 先于 Poll 被应用
        });

        var sb = new StringBuilder(raw);
        foreach (var (pos, text) in edits)
            sb.Insert(pos, text);
        return sb.ToString();
    }

    private static IEnumerable<StatementSyntax> EnumerateInstrumentableStatements(SyntaxNode root)
    {
        foreach (var stmt in root.DescendantNodes().OfType<StatementSyntax>())
        {
            if (stmt is BlockSyntax or EmptyStatementSyntax or LabeledStatementSyntax)
                continue;
            yield return stmt;
        }
    }

    private static void TryInsertPollInLoopBody(
        StatementSyntax loop, string raw, List<(int Pos, string Text)> edits)
    {
        StatementSyntax? body = loop switch
        {
            WhileStatementSyntax w => w.Statement,
            ForStatementSyntax f => f.Statement,
            ForEachStatementSyntax fe => fe.Statement,
            _ => null
        };
        if (body is not BlockSyntax block || block.Statements.Count == 0)
            return;

        var first = block.Statements[0];
        var insertAt = GetLineStart(raw, first.SpanStart);
        var indent = GetIndentAt(raw, first.SpanStart);
        edits.Add((insertAt, indent + "await __flyDbg.PollAsync();\n"));
    }

    private static bool ContainsDelayAwait(StatementSyntax stmt)
    {
        foreach (var awaitExpr in stmt.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>())
        {
            var text = awaitExpr.Expression.ToString();
            if (text.Contains("Delay(", StringComparison.Ordinal)
                || text.Contains("Task.Delay", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string BuildLineCall(int line, IReadOnlyList<string> locals)
    {
        var sb = new StringBuilder();
        sb.Append("await __flyDbg.LineAsync(").Append(line);
        foreach (var name in locals)
        {
            if (!IsSafeIdent(name)) continue;
            sb.Append(", (\"").Append(name).Append("\", (object?)").Append(name).Append(')');
        }
        sb.Append(");");
        return sb.ToString();
    }

    /// <summary>仅收集在注入点「可见」的局部变量（同块内、声明在前；含外层块与 foreach 迭代变量）。</summary>
    private static List<string> CollectLocalsBefore(SyntaxNode root, int position)
    {
        var names = new List<string>();
        var safePos = Math.Min(Math.Max(0, position), Math.Max(0, root.FullSpan.End - 1));
        SyntaxNode? node = root.FindToken(safePos).Parent;
        while (node != null && node is not StatementSyntax and not GlobalStatementSyntax)
            node = node.Parent;

        for (var n = node; n != null; n = n.Parent)
        {
            if (n is BlockSyntax block)
            {
                foreach (var s in block.Statements)
                {
                    if (s.SpanStart >= position) break;
                    AddDeclsFromStatement(s, names);
                }
            }
            else if (n is CompilationUnitSyntax cu)
            {
                foreach (var member in cu.Members)
                {
                    if (member is not GlobalStatementSyntax gs) continue;
                    if (gs.SpanStart >= position) break;
                    AddDeclsFromStatement(gs.Statement, names);
                }
            }
            else if (n is ForEachStatementSyntax fe
                     && position >= fe.Statement.SpanStart
                     && position <= fe.Statement.Span.End)
            {
                AddName(fe.Identifier.ValueText, names);
            }
            else if (n is ForStatementSyntax fs
                     && position >= fs.Statement.SpanStart
                     && position <= fs.Statement.Span.End
                     && fs.Declaration != null)
            {
                foreach (var v in fs.Declaration.Variables)
                    AddName(v.Identifier.ValueText, names);
            }
        }

        return names;
    }

    private static void AddDeclsFromStatement(StatementSyntax stmt, List<string> names)
    {
        if (stmt is LocalDeclarationStatementSyntax local)
        {
            foreach (var v in local.Declaration.Variables)
                AddName(v.Identifier.ValueText, names);
        }
    }

    private static void AddName(string id, List<string> names)
    {
        if (IsSafeIdent(id) && !names.Contains(id, StringComparer.Ordinal))
            names.Add(id);
    }

    private static int GetLineStart(string source, int position)
    {
        var lineStart = Math.Clamp(position, 0, source.Length);
        while (lineStart > 0 && source[lineStart - 1] is not ('\n' or '\r'))
            lineStart--;
        return lineStart;
    }

    private static string GetIndentAt(string source, int position)
    {
        var lineStart = GetLineStart(source, position);
        var i = lineStart;
        while (i < source.Length && source[i] is ' ' or '\t')
            i++;
        return source[lineStart..i];
    }

    private static bool IsSafeIdent(string name)
        => name.Length > 0
           && (char.IsLetter(name[0]) || name[0] == '_')
           && name.All(c => char.IsLetterOrDigit(c) || c == '_')
           && name is not ("ct" or "Delay" or "Fly" or "Console" or "Task");
}
