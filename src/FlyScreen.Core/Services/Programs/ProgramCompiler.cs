using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

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
            var result = ProgramScriptHost.Compile(prepared, emitAssembly: false);
            var list = ProgramScriptHost.MapDiagnostics(result.Diagnostics, fileName, result.LineOffset);
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
}

/// <summary>一次脚本编译的结果（可带 PE，供进程内加载运行）。</summary>
public sealed class ProgramScriptCompileResult
{
    public bool Success { get; init; }
    public int LineOffset { get; init; }
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
    {
        var (code, lineOffset) = WrapAsEntry(preparedUserSource);
        var parseOptions = new CSharpParseOptions(
            languageVersion: LanguageVersion.CSharp12,
            kind: SourceCodeKind.Regular);
        var tree = CSharpSyntaxTree.ParseText(code, parseOptions, path: "main.cs", encoding: Encoding.UTF8);

        var compilation = CSharpCompilation.Create(
            assemblyName: "FlyScreen.UserProgram",
            syntaxTrees: [tree],
            references: GetSharedReferences(),
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
                Diagnostics = emit.Diagnostics
            };
        }

        return new ProgramScriptCompileResult
        {
            Success = true,
            LineOffset = lineOffset,
            Diagnostics = emit.Diagnostics,
            AssemblyBytes = peStream.ToArray()
        };
    }

    public static async Task RunCompiledAsync(
        byte[] assemblyBytes,
        CancellationToken ct,
        ProgramDebugGate gate)
    {
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        using var pe = new MemoryStream(assemblyBytes, writable: false);
        var alc = new AssemblyLoadContext("FlyScreen.UserProgram." + Guid.NewGuid().ToString("N"), isCollectible: true);
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

    public static List<ProgramDiagnostic> MapDiagnostics(
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

    /// <summary>
    /// 将用户脚本包进静态入口类，避免 CSharpScript globals / 运行时 CoreLib CreateFromFile。
    /// 返回 (完整源码, 用户代码相对完整源码的行偏移)。
    /// </summary>
    public static (string Code, int LineOffset) WrapAsEntry(string preparedUserSource)
    {
        const string prefix =
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

            namespace FlyScreen.Services.Programs.Scripting
            {
                public static class __FlyScriptEntry
                {
                    public static async Task RunAsync(CancellationToken ct, ProgramDebugGate __flyDbg)
                    {
                        Task Delay(int milliseconds) => Task.Delay(milliseconds, ct);

            """;
        const string suffix =
            """

                    }
                }
            }
            """;

        var lineOffset = 0;
        foreach (var ch in prefix)
        {
            if (ch == '\n') lineOffset++;
        }

        return (prefix + (preparedUserSource ?? "") + suffix, lineOffset);
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
