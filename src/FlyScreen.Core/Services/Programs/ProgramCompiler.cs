using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace FlyScreen.Services.Programs;

public sealed class ProgramDiagnostic
{
    public string Severity { get; init; } = "error";
    public string Id { get; init; } = "";
    public string Message { get; init; } = "";
    public int Line { get; init; }
    public int Column { get; init; }
    public string FileName { get; init; } = "main.cs";

    public string FormatLine()
        => $"{FileName}({Line},{Column}): {Severity} {Id}: {Message}";
}

public sealed class ProgramCompileResult
{
    public bool Success { get; init; }
    public IReadOnlyList<ProgramDiagnostic> Diagnostics { get; init; } = [];

    public string FormatReport(string? header = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(header))
            sb.AppendLine(header);
        foreach (var d in Diagnostics)
            sb.AppendLine(d.FormatLine());
        sb.AppendLine(Success
            ? FlyScreen.Helpers.Localization.Loc.T("Prog.BuildOk")
            : FlyScreen.Helpers.Localization.Loc.T("Prog.BuildFail"));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>程序「生成」：对 C# 脚本做 Roslyn 编译诊断（对齐 VS 生成反馈）。</summary>
public static class ProgramCompiler
{
    public static ProgramCompileResult Compile(ProgramLanguage lang, string source, string fileName = "main.cs")
    {
        if (lang != ProgramLanguage.CSharp)
        {
            return new ProgramCompileResult
            {
                Success = false,
                Diagnostics =
                [
                    new ProgramDiagnostic
                    {
                        Severity = "error",
                        Id = "FLY0001",
                        Message = "当前「生成」仅支持 C# (.NET) 脚本。",
                        Line = 1,
                        Column = 1,
                        FileName = fileName
                    }
                ]
            };
        }

        var code = ProgramScriptHost.PrepareSource(source ?? "");
        var options = ProgramScriptHost.CreateScriptOptions();
        var script = CSharpScript.Create(code, options, typeof(InProcessProgramExecutor.CSharpScriptGlobals));
        var diags = script.Compile();
        var list = MapDiagnostics(diags, fileName);
        var hasError = list.Any(d => d.Severity == "error");
        return new ProgramCompileResult
        {
            Success = !hasError,
            Diagnostics = list
        };
    }

    private static List<ProgramDiagnostic> MapDiagnostics(ImmutableArray<Diagnostic> diags, string fileName)
    {
        var list = new List<ProgramDiagnostic>();
        foreach (var d in diags)
        {
            if (d.Severity is not DiagnosticSeverity.Error and not DiagnosticSeverity.Warning)
                continue;
            var line = 1;
            var col = 1;
            var span = d.Location.GetLineSpan();
            if (span.IsValid)
            {
                line = span.StartLinePosition.Line + 1;
                col = span.StartLinePosition.Character + 1;
            }
            list.Add(new ProgramDiagnostic
            {
                Severity = d.Severity == DiagnosticSeverity.Error ? "error" : "warning",
                Id = d.Id,
                Message = d.GetMessage(),
                Line = line,
                Column = col,
                FileName = fileName
            });
        }
        return list;
    }
}

/// <summary>C# 脚本共享编译/运行选项。</summary>
public static class ProgramScriptHost
{
    public static ScriptOptions CreateScriptOptions()
        => ScriptOptions.Default
            .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)
            .AddReferences(
                typeof(object).Assembly,
                typeof(Console).Assembly,
                typeof(Enumerable).Assembly,
                typeof(Task).Assembly,
                typeof(StringBuilder).Assembly,
                typeof(Fly).Assembly,
                typeof(ProgramDebugGate).Assembly)
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Text",
                "System.Threading",
                "System.Threading.Tasks",
                "FlyScreen.Services.Programs");

    /// <summary>Task.Delay(x) → Task.Delay(x, ct)。</summary>
    public static string PrepareSource(string source)
        => System.Text.RegularExpressions.Regex.Replace(
            source ?? "",
            @"Task\s*\.\s*Delay\s*\(\s*(?<arg>[^,\)]+?)\s*\)",
            m => $"Task.Delay({m.Groups["arg"].Value.Trim()}, ct)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
