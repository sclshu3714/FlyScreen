using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

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
        => Compile(lang, source, fileName, extras: null, extraReferences: null);

    public static ProgramCompileResult Compile(
        ProgramLanguage lang,
        string source,
        string fileName,
        IReadOnlyList<(string Path, string Text)>? extras,
        IReadOnlyList<MetadataReference>? extraReferences)
    {
        if (lang != ProgramLanguage.CSharp)
        {
            // 非 C#：不走 Roslyn；「生成」视为跳过（成功），避免误报 FLY0001 失败
            return new ProgramCompileResult
            {
                Success = true,
                Diagnostics =
                [
                    new ProgramDiagnostic
                    {
                        Severity = "info",
                        Id = "FLY0010",
                        Message =
                            $"「生成」仅对 C# 做编译检查；当前为 {ProgramLanguageInfo.DisplayName(lang)}，已跳过。",
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
            var result = ProgramScriptHost.Compile(
                prepared, emitAssembly: false, extras, extraReferences, entryPath: fileName);
            var list = ProgramScriptHost.MapDiagnostics(
                result.Diagnostics, fileName, result.LineOffset, result.EntryPath);
            var hasError = list.Any(d => d.Severity == "error");
            return new ProgramCompileResult
            {
                Success = result.Success && !hasError,
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

    /// <summary>按程序清单编译（含 ExtraFiles / LocalDlls / 可选 NuGet 引用路径）。</summary>
    public static async Task<ProgramCompileResult> CompileProgramAsync(
        ProgramCatalog catalog,
        UserProgram prog,
        string? entrySourceOverride = null,
        CancellationToken ct = default)
    {
        catalog.NormalizeManifest(prog);
        var entryName = string.IsNullOrWhiteSpace(prog.FileName) ? "main.cs" : prog.FileName;
        var source = entrySourceOverride ?? catalog.ReadSource(prog);
        var extras = catalog.ReadExtraSources(prog)
            .Select(x => (x.RelativePath, x.Content))
            .ToList();

        var refs = new List<MetadataReference>();
        foreach (var dll in catalog.ResolveLocalDllPaths(prog))
        {
            if (ProgramScriptHost.TryCreateReferenceFromFile(dll, out var reference, out var error, out var nonManaged))
            {
                refs.Add(reference!);
                continue;
            }

            // 无 CLR 元数据：跳过（refs/ 误含 WinAppSDK 原生 DLL 时不应导致生成失败）
            if (nonManaged)
            {
                AppLog.Warn("脚本编译: " + error);
                continue;
            }

            return Fail($"本地 DLL 引用失败「{Path.GetFileName(dll)}」: {error}", entryName);
        }

        if (prog.Packages.Count > 0)
        {
            if (ProgramNugetResolver.CanRestoreOnline)
            {
                var nuget = await ProgramNugetResolver.ResolveAsync(catalog, prog, ct).ConfigureAwait(false);
                if (!nuget.Success)
                    return Fail(nuget.Error ?? "NuGet 还原失败", entryName);
                foreach (var path in nuget.ReferencePaths)
                {
                    if (ProgramScriptHost.TryCreateReferenceFromFile(path, out var reference, out var error, out var nonManaged))
                    {
                        refs.Add(reference!);
                        continue;
                    }

                    if (nonManaged)
                    {
                        AppLog.Warn("脚本编译: " + error);
                        continue;
                    }

                    return Fail($"NuGet 引用失败「{Path.GetFileName(path)}」: {error}", entryName);
                }
            }
            else
            {
                // 手机：尝试缓存路径（通常不存在）；否则要求 refs/
                var cached = ProgramNugetResolver.TryReadRefsCache(catalog, prog);
                if (cached.Count == 0 && catalog.ResolveLocalDllPaths(prog).Count == 0)
                {
                    return Fail(
                        "程序声明了 NuGet 包，但手机端不能在线还原。请把依赖 DLL 放入 refs/ 并在程序依赖中登记。",
                        entryName);
                }
                foreach (var path in cached)
                {
                    if (ProgramScriptHost.TryCreateReferenceFromFile(path, out var reference, out _, out _))
                        refs.Add(reference!);
                }
            }
        }

        return Compile(prog.Language, source, entryName, extras, refs);
    }

    private static ProgramCompileResult Fail(string message, string fileName)
        => new()
        {
            Success = false,
            Diagnostics =
            [
                new ProgramDiagnostic
                {
                    Severity = "error",
                    Id = "FLY0003",
                    Message = message,
                    Line = 1,
                    Column = 1,
                    FileName = fileName
                }
            ]
        };
}

/// <summary>一次脚本编译的结果（可带 PE，供进程内加载运行）。</summary>
public sealed class ProgramScriptCompileResult
{
    public bool Success { get; init; }
    public int LineOffset { get; init; }
    public string EntryPath { get; init; } = "main.cs";
    public ImmutableArray<Diagnostic> Diagnostics { get; init; } = ImmutableArray<Diagnostic>.Empty;
    public byte[]? AssemblyBytes { get; init; }
}

/// <summary>
/// C# 用户程序：用嵌入的 net10.0 <c>reference assemblies</c> + <see cref="CSharpCompilation"/> 编译。
/// 避免 CSharp.Scripting 在 Android/单文件上对运行时 <c>System.Private.CoreLib</c> 做 CreateFromFile。
/// </summary>
public static class ProgramScriptHost
{
    public const string EntryTypeName = "FlyScreen.Services.Programs.Scripting.__FlyScriptEntry";
    public const string EntryMethodName = "RunAsync";

    /// <summary>保持从内存元数据创建的对象存活，避免引用失效。</summary>
    private static readonly List<object> MetadataKeepAlive = [];

    private static readonly object ReferencesGate = new();
    private static ImmutableArray<MetadataReference>? CachedReferences;

    /// <summary>
    /// 可选：宿主在启动时注册额外 PE（如 Android 上从 MauiAsset 提供的宿主程序集）。
    /// key = 简单名（无 .dll）。注册会失效引用缓存，须在首次编译前调用。
    /// </summary>
    private static readonly Dictionary<string, byte[]> ExtraImages =
        new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterReferenceImage(string simpleName, byte[] peImage)
    {
        if (string.IsNullOrWhiteSpace(simpleName) || peImage is not { Length: > 0 })
            return;
        lock (ExtraImages)
            ExtraImages[simpleName.Trim()] = peImage;
        lock (ReferencesGate)
            CachedReferences = null;
    }

    private static ImmutableArray<MetadataReference> GetSharedReferences()
    {
        lock (ReferencesGate)
        {
            if (CachedReferences is { } cached)
                return cached;
            var built = BuildReferences();
            CachedReferences = built;
            return built;
        }
    }

    public static ProgramScriptCompileResult Compile(string preparedUserSource, bool emitAssembly)
        => Compile(preparedUserSource, emitAssembly, extras: null, extraReferences: null, entryPath: "main.cs");

    public static ProgramScriptCompileResult Compile(
        string preparedUserSource,
        bool emitAssembly,
        IReadOnlyList<(string Path, string Text)>? extras,
        IReadOnlyList<MetadataReference>? extraReferences,
        string entryPath = "main.cs")
    {
        var entry = string.IsNullOrWhiteSpace(entryPath) ? "main.cs" : entryPath.Replace('\\', '/');
        var (code, lineOffset) = WrapAsEntry(preparedUserSource);
        var parseOptions = new CSharpParseOptions(
            languageVersion: LanguageVersion.CSharp12,
            kind: SourceCodeKind.Regular);
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(code, parseOptions, path: entry, encoding: Encoding.UTF8)
        };

        if (extras != null)
        {
            foreach (var (path, text) in extras)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var p = path.Replace('\\', '/');
                if (string.Equals(p, entry, StringComparison.OrdinalIgnoreCase)) continue;
                trees.Add(CSharpSyntaxTree.ParseText(
                    text ?? "", parseOptions, path: p, encoding: Encoding.UTF8));
            }
        }

        var references = MergeReferences(GetSharedReferences(), extraReferences);

        var compilation = CSharpCompilation.Create(
            assemblyName: "FlyScreen.UserProgram",
            syntaxTrees: trees,
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false,
                concurrentBuild: false,
                deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable));

        if (!emitAssembly)
        {
            var diags = compilation.GetDiagnostics();
            return new ProgramScriptCompileResult
            {
                Success = !diags.Any(d => d.Severity == DiagnosticSeverity.Error),
                LineOffset = lineOffset,
                EntryPath = entry,
                Diagnostics = diags
            };
        }

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!emit.Success)
        {
            return new ProgramScriptCompileResult
            {
                Success = false,
                LineOffset = lineOffset,
                EntryPath = entry,
                Diagnostics = emit.Diagnostics
            };
        }

        return new ProgramScriptCompileResult
        {
            Success = true,
            LineOffset = lineOffset,
            EntryPath = entry,
            Diagnostics = emit.Diagnostics,
            AssemblyBytes = peStream.ToArray()
        };
    }

    /// <summary>
    /// 从磁盘创建托管程序集元数据引用。
    /// 原生 / WinRT / 无 CLR 元数据的 PE（如 WinAppSDK 的 <c>Microsoft.Windows.Widgets.dll</c>）会抛
    /// <see cref="BadImageFormatException"/>，避免落入 Roslyn 后变成 CS0009。
    /// </summary>
    public static MetadataReference CreateReferenceFromFile(string dllPath)
    {
        if (!TryCreateReferenceFromFile(dllPath, out var reference, out var error, out _))
            throw new BadImageFormatException(error ?? "无法创建元数据引用", dllPath);
        return reference!;
    }

    /// <summary>
    /// 尝试创建托管元数据引用；非托管 PE / 坏映像返回 false（供 refs/ 批量扫描跳过）。
    /// <paramref name="isNonManaged"/> 为 true 时表示无 CLR 元数据（可安全跳过，勿当致命错误）。
    /// </summary>
    public static bool TryCreateReferenceFromFile(
        string dllPath,
        out MetadataReference? reference,
        out string? error,
        out bool isNonManaged)
    {
        reference = null;
        error = null;
        isNonManaged = false;
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            error = "引用 DLL 不存在: " + dllPath;
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(dllPath);
        }
        catch (Exception ex)
        {
            error = $"读取失败「{Path.GetFileName(dllPath)}」: {ex.Message}";
            return false;
        }

        if (!HasManagedMetadata(bytes))
        {
            isNonManaged = true;
            error =
                $"「{Path.GetFileName(dllPath)}」不是托管程序集（无 CLR 元数据，可能是原生/WinRT DLL），已跳过编译引用。";
            return false;
        }

        try
        {
            reference = CreateFromImageBytes(bytes, Path.GetFileNameWithoutExtension(dllPath));
            return true;
        }
        catch (Exception ex)
        {
            error = $"「{Path.GetFileName(dllPath)}」元数据无效: {ex.Message}";
            return false;
        }
    }

    /// <summary>PE 是否含 CLI/CLR 元数据（可被 Roslyn 作 MetadataReference）。</summary>
    public static bool HasManagedMetadata(byte[]? peImage)
    {
        if (peImage is not { Length: >= 64 })
            return false;
        try
        {
            using var ms = new MemoryStream(peImage, writable: false);
            using var pe = new PEReader(ms);
            return pe.HasMetadata && pe.PEHeaders?.CorHeader != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasManagedMetadata(string dllPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
                return false;
            return HasManagedMetadata(File.ReadAllBytes(dllPath));
        }
        catch
        {
            return false;
        }
    }

    private static ImmutableArray<MetadataReference> MergeReferences(
        ImmutableArray<MetadataReference> shared,
        IReadOnlyList<MetadataReference>? extra)
    {
        if (extra is not { Count: > 0 })
            return shared;
        var list = new List<MetadataReference>(shared);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in shared)
        {
            var dn = r.Display;
            if (!string.IsNullOrEmpty(dn))
                seen.Add(Path.GetFileNameWithoutExtension(dn));
        }
        foreach (var r in extra)
        {
            if (r == null) continue;
            var dn = r.Display ?? "";
            var key = Path.GetFileNameWithoutExtension(dn);
            if (!string.IsNullOrEmpty(key) && !seen.Add(key))
                continue;
            list.Add(r);
        }
        return list.ToImmutableArray();
    }

    public static Task RunCompiledAsync(
        byte[] assemblyBytes,
        CancellationToken ct,
        ProgramDebugGate gate)
        => RunCompiledAsync(assemblyBytes, ct, gate, assemblyProbePaths: null);

    /// <param name="assemblyProbePaths">
    /// 运行时依赖探测目录（如程序 <c>refs/</c>、WindowsDesktop 共享框架）。
    /// 玄鸟宿主未带 WPF 时，脚本引用的 PresentationFramework 等由此加载。
    /// </param>
    public static async Task RunCompiledAsync(
        byte[] assemblyBytes,
        CancellationToken ct,
        ProgramDebugGate gate,
        IReadOnlyList<string>? assemblyProbePaths)
    {
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        using var pe = new MemoryStream(assemblyBytes, writable: false);
        var probes = NormalizeProbePaths(assemblyProbePaths);
        var alc = new ScriptLoadContext(
            "FlyScreen.UserProgram." + Guid.NewGuid().ToString("N"),
            probes);
        try
        {
            var asm = alc.LoadFromStream(pe);
            var type = asm.GetType(EntryTypeName, throwOnError: true)!;
            var method = type.GetMethod(EntryMethodName, BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(EntryTypeName, EntryMethodName);
            var result = method.Invoke(null, [ct, gate]);
            if (result is Task task)
                await task.ConfigureAwait(false);
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>程序 refs/ + 本机 Microsoft.WindowsDesktop.App 共享框架目录。</summary>
    public static List<string> BuildRuntimeAssemblyProbePaths(string? programFolder)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var full = Path.GetFullPath(dir);
                if (!Directory.Exists(full) || !seen.Add(full)) return;
                list.Add(full);
            }
            catch
            {
                // ignore
            }
        }

        if (!string.IsNullOrWhiteSpace(programFolder))
            Add(Path.Combine(programFolder, "refs"));

        try
        {
            var dotnetShared = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared");
            foreach (var pack in new[] { "Microsoft.WindowsDesktop.App", "Microsoft.NETCore.App" })
            {
                var sharedRoot = Path.Combine(dotnetShared, pack);
                if (!Directory.Exists(sharedRoot)) continue;
                foreach (var dir in Directory.EnumerateDirectories(sharedRoot)
                             .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                    Add(dir);
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private static List<string> NormalizeProbePaths(IReadOnlyList<string>? paths)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths == null) return list;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                var full = Path.GetFullPath(p);
                if (Directory.Exists(full) && seen.Add(full))
                    list.Add(full);
            }
            catch
            {
                // ignore
            }
        }
        return list;
    }

    private sealed class ScriptLoadContext : AssemblyLoadContext
    {
        private readonly List<string> _probes;

        public ScriptLoadContext(string name, List<string> probes)
            : base(name, isCollectible: true)
        {
            _probes = probes;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var simple = assemblyName.Name;
            if (string.IsNullOrEmpty(simple))
                return null;

            // 与宿主共享的契约 / 已加载程序集，避免类型分裂
            if (ShouldPreferDefault(simple))
            {
                try
                {
                    return Default.LoadFromAssemblyName(assemblyName);
                }
                catch (FileNotFoundException)
                {
                    /* fall through */
                }
                catch (FileLoadException)
                {
                    /* fall through */
                }
            }

            var fileName = simple + ".dll";
            foreach (var dir in _probes)
            {
                try
                {
                    var path = Path.Combine(dir, fileName);
                    if (!File.Exists(path)) continue;
                    // 读字节加载，避免把框架路径钉死在可回收 ALC 上
                    var bytes = File.ReadAllBytes(path);
                    return LoadFromStream(new MemoryStream(bytes));
                }
                catch
                {
                    // try next
                }
            }

            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch
            {
                return null;
            }
        }

        private static bool ShouldPreferDefault(string name)
        {
            // WPF / Desktop 程序集必须从 WindowsDesktop 探测目录加载，不能走宿主 Default（Maui 无）
            if (name is "PresentationFramework" or "PresentationCore" or "WindowsBase"
                or "System.Xaml" or "System.IO.Packaging" or "DirectWriteForwarder"
                or "UIAutomationTypes" or "UIAutomationProvider")
                return false;
            if (name.StartsWith("PresentationFramework.", StringComparison.Ordinal))
                return false;

            if (name is "FlyScreen.Core" or "FlyScreen.ToolSdk" or "FlyScreen.Assets"
                or "netstandard" or "mscorlib")
                return true;
            if (name.StartsWith("System.", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.", StringComparison.Ordinal))
                return true;
            return false;
        }
    }

    public static List<ProgramDiagnostic> MapDiagnostics(
        ImmutableArray<Diagnostic> diags, string fileName, int lineOffset)
        => MapDiagnostics(diags, fileName, lineOffset, entryPath: fileName);

    public static List<ProgramDiagnostic> MapDiagnostics(
        ImmutableArray<Diagnostic> diags, string entryFileName, int lineOffset, string? entryPath)
    {
        var entry = string.IsNullOrWhiteSpace(entryPath) ? entryFileName : entryPath.Replace('\\', '/');
        var list = new List<ProgramDiagnostic>();
        foreach (var d in diags)
        {
            if (d.Severity is not DiagnosticSeverity.Error and not DiagnosticSeverity.Warning)
                continue;
            var line = 1;
            var col = 1;
            // Prefer mapped span so WrapAsEntry #line directives surface as user-file lines.
            var mapped = d.Location.GetMappedLineSpan();
            var span = mapped.IsValid ? mapped : d.Location.GetLineSpan();
            var path = span.Path;
            if (string.IsNullOrEmpty(path))
                path = entryFileName;
            path = path.Replace('\\', '/');
            var isEntry = string.Equals(path, entry, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(Path.GetFileName(path), Path.GetFileName(entry), StringComparison.OrdinalIgnoreCase)
                          || string.Equals(Path.GetFileName(path), Path.GetFileName(entryFileName), StringComparison.OrdinalIgnoreCase);
            if (span.IsValid)
            {
                line = span.StartLinePosition.Line + 1;
                // Legacy path: when #line is absent, LineOffset still strips the fixed wrapper header.
                if (isEntry && lineOffset != 0)
                    line = Math.Max(1, line - lineOffset);
                col = span.StartLinePosition.Character + 1;
            }
            list.Add(new ProgramDiagnostic
            {
                Severity = d.Severity == DiagnosticSeverity.Error ? "error" : "warning",
                Id = d.Id,
                Message = d.GetMessage(),
                Line = line,
                Column = col,
                FileName = isEntry ? entryFileName : path
            });
        }
        return list;
    }

    /// <summary>
    /// 将用户脚本包进静态入口类，避免 CSharpScript globals / 运行时 CoreLib CreateFromFile。
    /// 入口顶部的 <c>using</c> / <c>global using</c> / <c>using static</c> 会提升到方法外（与常规 C# 一致）。
    /// 通过 <c>#line</c> 映射回用户文件行号，故 <see cref="LineOffset"/> 恒为 0。
    /// </summary>
    public static (string Code, int LineOffset) WrapAsEntry(string preparedUserSource)
    {
        var source = preparedUserSource ?? "";
        var (usingsText, bodyText, bodyStartLine) = SplitLeadingUsings(source);

        // 固定头：框架 global using（用户 using 插在其后、namespace 之前）
        const string header =
            """
            #nullable enable
            global using System;
            global using System.Collections.Generic;
            global using System.Linq;
            global using System.Text;
            global using System.Threading;
            global using System.Threading.Tasks;
            global using FlyScreen.Services;
            global using FlyScreen.Services.Programs;

            """;

        const string methodOpen =
            """
            namespace FlyScreen.Services.Programs.Scripting
            {
                public static class __FlyScriptEntry
                {
                    public static async Task RunAsync(CancellationToken ct, ProgramDebugGate __flyDbg)
                    {
                        Task Delay(int milliseconds) => Task.Delay(milliseconds, ct);

            """;

        const string methodClose =
            """

                    }
                }
            }
            """;

        var sb = new StringBuilder(header.Length + source.Length + 256);
        sb.Append(header);

        // 提升用户 using 到编译单元级（方法外）
        if (usingsText.Length > 0)
        {
            sb.AppendLine("#line 1");
            sb.Append(usingsText);
            if (!usingsText.EndsWith('\n'))
                sb.AppendLine();
        }

        sb.AppendLine("#line hidden");
        sb.Append(methodOpen);
        sb.Append("#line ").Append(bodyStartLine).AppendLine();
        sb.Append(bodyText);
        if (bodyText.Length > 0 && !bodyText.EndsWith('\n'))
            sb.AppendLine();
        sb.AppendLine("#line default");
        sb.Append(methodClose);

        // #line 已映射到用户行号，MapDiagnostics 无需再减偏移
        return (sb.ToString(), 0);
    }

    /// <summary>
    /// 拆分入口顶部的 using 指令与其余语句。
    /// 返回 (using 段原文含前置注释, 方法体段, 方法体在用户文件中的起始行 1-based)。
    /// </summary>
    internal static (string UsingsText, string BodyText, int BodyStartLine) SplitLeadingUsings(string source)
    {
        if (string.IsNullOrEmpty(source))
            return ("", "", 1);

        try
        {
            var tree = CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(languageVersion: LanguageVersion.CSharp12, kind: SourceCodeKind.Regular));
            var root = tree.GetCompilationUnitRoot();
            var usings = root.Usings;
            if (usings.Count == 0)
                return ("", source, 1);

            // 从文件头到最后一条 using（含其 trailing trivia），保证 #line 1 对齐用户文件
            var splitEnd = usings[^1].FullSpan.End;
            splitEnd = Math.Clamp(splitEnd, 0, source.Length);
            var usingsText = source[..splitEnd];
            var bodyText = source[splitEnd..];

            var pos = tree.GetLineSpan(TextSpan.FromBounds(splitEnd, splitEnd)).StartLinePosition;
            var bodyStartLine = pos.Line + 1; // 1-based；splitEnd 通常已在下一行行首
            if (bodyStartLine < 1) bodyStartLine = 1;
            return (usingsText, bodyText, bodyStartLine);
        }
        catch
        {
            // 解析失败时不提升，保持旧行为（整段进方法体）
            return ("", source, 1);
        }
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var list = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 嵌入的 net10.0 reference assemblies（Android / 单文件唯一可靠来源）
        foreach (var (name, reference) in LoadEmbeddedRefImages())
        {
            if (!seen.Add(name)) continue;
            list.Add(reference);
        }

        if (list.Count == 0)
            AppLog.Warn("脚本编译：未加载到任何嵌入 ref（Microsoft.NETCore.App.Ref）。Android 上将无法编译 C# 程序。");

        // 2) 额外注册的 PE（如 MauiAsset 宿主）
        lock (ExtraImages)
        {
            foreach (var (name, bytes) in ExtraImages)
            {
                if (!seen.Add(name)) continue;
                try
                {
                    list.Add(CreateFromImageBytes(bytes, name));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"脚本编译：额外引用「{name}」失败: {ex.Message}");
                    seen.Remove(name);
                }
            }
        }

        // 3) 宿主程序集（Fly API / DebugGate）
        foreach (var asm in new[] { typeof(Fly).Assembly, typeof(ProgramDebugGate).Assembly })
            TryAddHostAssembly(list, seen, asm);

        return list.ToImmutableArray();
    }

    private static IEnumerable<(string Name, MetadataReference Reference)> LoadEmbeddedRefImages()
    {
        const string prefix = "FlyScreen.ScriptRefs.";
        var asm = typeof(ProgramScriptHost).Assembly;
        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var fileName = resourceName[prefix.Length..];
            if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;
            var simpleName = fileName[..^4];
            byte[] bytes;
            try
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }
            catch
            {
                continue;
            }

            if (bytes.Length == 0) continue;
            MetadataReference reference;
            try
            {
                reference = CreateFromImageBytes(bytes, simpleName);
            }
            catch
            {
                continue;
            }

            yield return (simpleName, reference);
        }
    }

    private static void TryAddHostAssembly(
        List<MetadataReference> list, HashSet<string> seen, Assembly asm)
    {
        string? key = null;
        try
        {
            key = asm.GetName().Name;
            if (string.IsNullOrEmpty(key) || !seen.Add(key))
                return;
            list.Add(CreateHostMetadataReference(asm));
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(key))
                seen.Remove(key!);
            AppLog.Warn($"脚本编译：宿主程序集「{key ?? asm.FullName}」引用失败: {ex.Message}");
        }
    }

    private static MetadataReference CreateHostMetadataReference(Assembly assembly)
    {
        try
        {
            var loc = assembly.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                return MetadataReference.CreateFromFile(loc);
        }
        catch
        {
            // continue
        }

        var name = assembly.GetName().Name;
        if (!string.IsNullOrEmpty(name))
        {
            lock (ExtraImages)
            {
                if (ExtraImages.TryGetValue(name, out var extra))
                    return CreateFromImageBytes(extra, name);
            }

            var path = ResolvePathByFileName(name + ".dll");
            if (!string.IsNullOrEmpty(path))
                return MetadataReference.CreateFromFile(path);
        }

        // 桌面/部分宿主：内存元数据；Android Mono 上常不可用
        return CreateFromRawMetadata(assembly);
    }

    private static MetadataReference CreateFromImageBytes(byte[] peImage, string? display)
    {
        // CreateFromImage 需要保持底层图像；用 MetadataKeepAlive 钉住模块
        var module = ModuleMetadata.CreateFromImage(peImage);
        var asmMeta = AssemblyMetadata.Create(module);
        lock (MetadataKeepAlive)
        {
            MetadataKeepAlive.Add(peImage);
            MetadataKeepAlive.Add(module);
            MetadataKeepAlive.Add(asmMeta);
        }

        return asmMeta.GetReference(display: display);
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
        try { Add(Path.GetDirectoryName(typeof(object).Assembly.Location)); } catch { /* ignore */ }
        try { Add(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()); } catch { /* ignore */ }

        return set;
    }

    private static unsafe MetadataReference CreateFromRawMetadata(Assembly assembly)
    {
        if (!assembly.TryGetRawMetadata(out byte* blob, out var length) || blob == null || length <= 0)
        {
            throw new NotSupportedException(
                $"无法为程序集「{assembly.GetName().Name}」创建元数据引用（无磁盘路径且无法读取内存元数据）。" +
                "Android 请在启动时 ProgramScriptHost.RegisterReferenceImage 注册该 DLL。");
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
