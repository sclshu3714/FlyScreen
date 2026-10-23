using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
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

        try
        {
            var prepared = ProgramScriptHost.PrepareSource(source ?? "");
            var (code, lineOffset) = ProgramScriptHost.WrapAsEntry(prepared);
            var options = ProgramScriptHost.CreateScriptOptions();
            // 禁止传入 globalsType：单文件/部分宿主下其程序集 Location 为空，
            // Roslyn 会抛「无法对不含位置的程序集创建元数据引用」。
            var script = CSharpScript.Create(code, options);
            var diags = script.Compile();
            var list = MapDiagnostics(diags, fileName, lineOffset);
            var hasError = list.Any(d => d.Severity == "error");
            return new ProgramCompileResult
            {
                Success = !hasError,
                Diagnostics = list
            };
        }
        catch (Exception ex)
        {
            return new ProgramCompileResult
            {
                Success = false,
                Diagnostics =
                [
                    new ProgramDiagnostic
                    {
                        Severity = "error",
                        Id = "FLY0002",
                        Message = $"脚本编译环境失败: {ex.Message}",
                        Line = 1,
                        Column = 1,
                        FileName = fileName
                    }
                ]
            };
        }
    }

    private static List<ProgramDiagnostic> MapDiagnostics(
        ImmutableArray<Diagnostic> diags, string fileName, int lineOffset)
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
                line = Math.Max(1, span.StartLinePosition.Line + 1 - lineOffset);
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
    /// <summary>保持从内存元数据创建的 <see cref="AssemblyMetadata"/> 存活，避免引用失效。</summary>
    private static readonly List<object> MetadataKeepAlive = [];

    private static readonly Lazy<ImmutableArray<MetadataReference>> SharedReferences = new(BuildReferences);

    public static ScriptOptions CreateScriptOptions()
        => ScriptOptions.Default
            .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)
            // WithReferences 替换默认引用，避免宿主再按 Location 隐式加引用
            .WithReferences(SharedReferences.Value)
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Text",
                "System.Threading",
                "System.Threading.Tasks",
                "FlyScreen.Services.Programs");

    /// <summary>
    /// 将用户脚本包进无 globals 的入口委托，避免 CSharpScript 对 globals 程序集 CreateFromFile。
    /// 返回 (完整脚本, 用户代码相对完整脚本的行偏移)。
    /// </summary>
    public static (string Code, int LineOffset) WrapAsEntry(string preparedUserSource)
    {
        // 行偏移 = 前缀行数（用户第 1 行对应完整脚本第 LineOffset+1 行）
        const string prefix =
            """
            #nullable enable
            System.Func<System.Threading.CancellationToken, FlyScreen.Services.Programs.ProgramDebugGate, System.Threading.Tasks.Task> __flyEntry =
                async (ct, __flyDbg) =>
                {
                    System.Threading.Tasks.Task Delay(int milliseconds) => System.Threading.Tasks.Task.Delay(milliseconds, ct);

            """;
        const string suffix =
            """

                };
            return __flyEntry;
            """;

        // 诊断行号：用户第 1 行 = 完整脚本第 (newlineCount+1) 行 → 偏移 = prefix 中 \n 个数
        var lineOffset = 0;
        foreach (var ch in prefix)
        {
            if (ch == '\n') lineOffset++;
        }

        return (prefix + (preparedUserSource ?? "") + suffix, lineOffset);
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        Assembly[] assemblies =
        [
            typeof(object).Assembly,
            typeof(Console).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Task).Assembly,
            typeof(StringBuilder).Assembly,
            typeof(Uri).Assembly,
            typeof(Fly).Assembly,
            typeof(ProgramDebugGate).Assembly,
        ];

        var list = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in assemblies)
            TryAddReference(list, seen, asm);

        // 脚本常需显式 System.Runtime / netstandard
        TryAddBySimpleName(list, seen, "System.Runtime");
        TryAddBySimpleName(list, seen, "netstandard");
        TryAddBySimpleName(list, seen, "System.Collections");
        TryAddBySimpleName(list, seen, "System.Linq");
        TryAddBySimpleName(list, seen, "System.Console");

        return list.ToImmutableArray();
    }

    private static void TryAddReference(List<MetadataReference> list, HashSet<string> seen, Assembly asm)
    {
        try
        {
            var reference = CreateMetadataReference(asm);
            var key = asm.GetName().Name ?? asm.FullName ?? reference.Display ?? Guid.NewGuid().ToString("N");
            if (!seen.Add(key)) return;
            list.Add(reference);
        }
        catch
        {
            // 单个程序集失败不阻断其余引用
        }
    }

    private static void TryAddBySimpleName(List<MetadataReference> list, HashSet<string> seen, string simpleName)
    {
        if (!seen.Add(simpleName)) return;
        try
        {
            var path = ResolvePathByFileName(simpleName + ".dll");
            if (!string.IsNullOrEmpty(path))
                list.Add(MetadataReference.CreateFromFile(path));
            else
                seen.Remove(simpleName);
        }
        catch
        {
            seen.Remove(simpleName);
        }
    }

    private static string? ResolvePathByFileName(string fileName)
    {
        foreach (var dir in EnumerateProbeDirectories())
        {
            try
            {
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path)) return path;
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            {
                foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(path))
                        return path;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>
    /// 单文件发布 / 部分宿主下 <see cref="Assembly.Location"/> 为空，
    /// 不能再用 <c>AddReferences(Assembly)</c>（内部 CreateFromFile 会抛
    /// 「无法对不含位置的程序集创建元数据引用」）。
    /// </summary>
    private static MetadataReference CreateMetadataReference(Assembly assembly)
    {
        try
        {
            var loc = assembly.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                return MetadataReference.CreateFromFile(loc);
        }
        catch
        {
            // 继续回退
        }

        var resolved = ResolveAssemblyPath(assembly);
        if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
            return MetadataReference.CreateFromFile(resolved);

        return CreateFromRawMetadata(assembly);
    }

    private static string? ResolveAssemblyPath(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (string.IsNullOrEmpty(name)) return null;

        var fileName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".dll";

        foreach (var dir in EnumerateProbeDirectories())
        {
            try
            {
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                    return path;
            }
            catch
            {
                // ignore
            }
        }

        // 运行时 Trusted Platform Assemblies（框架程序集）
        try
        {
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            {
                foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(path))
                        return path;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static IEnumerable<string> EnumerateProbeDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p);
                if (Directory.Exists(full))
                    set.Add(full);
            }
            catch
            {
                // ignore
            }
        }

        Add(AppContext.BaseDirectory);
        Add(Path.GetDirectoryName(Environment.ProcessPath));
        Add(Path.GetDirectoryName(typeof(object).Assembly.Location));
        Add(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());

        return set;
    }

    private static unsafe MetadataReference CreateFromRawMetadata(Assembly assembly)
    {
        // System.Runtime.Loader：AssemblyExtensions.TryGetRawMetadata
        if (!assembly.TryGetRawMetadata(out byte* blob, out var length) || blob == null || length <= 0)
        {
            throw new NotSupportedException(
                $"无法为程序集「{assembly.GetName().Name}」创建元数据引用（无磁盘路径且无法读取内存元数据）。");
        }

        var module = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
        var asmMeta = AssemblyMetadata.Create(module);
        lock (MetadataKeepAlive)
        {
            MetadataKeepAlive.Add(module);
            MetadataKeepAlive.Add(asmMeta);
        }

        return asmMeta.GetReference(display: assembly.GetName().Name);
    }

    /// <summary>Task.Delay(x) → Task.Delay(x, ct)。</summary>
    public static string PrepareSource(string source)
        => System.Text.RegularExpressions.Regex.Replace(
            source ?? "",
            @"Task\s*\.\s*Delay\s*\(\s*(?<arg>[^,\)]+?)\s*\)",
            m => $"Task.Delay({m.Groups["arg"].Value.Trim()}, ct)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
