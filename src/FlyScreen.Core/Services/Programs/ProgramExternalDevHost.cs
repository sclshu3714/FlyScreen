using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace FlyScreen.Services.Programs;

/// <summary>
/// 外部 VS / VS Code / <c>dotnet run</c> 调试入口：用与玄鸟相同的
/// <see cref="ProgramScriptHost.WrapAsEntry"/> + <see cref="ProgramScriptHost.Compile"/> 跑 <c>main.cs</c>。
/// </summary>
public static class ProgramExternalDevHost
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static int _resolverInstalled;

    /// <summary>
    /// 在程序目录运行（含 <c>main.cs</c> / <c>program.json</c> / 可选 <c>settings.json</c>）。
    /// Ctrl+C 取消 <paramref name="ct"/>。
    /// </summary>
    public static async Task<int> RunAsync(string programFolder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(programFolder) || !Directory.Exists(programFolder))
        {
            Console.Error.WriteLine("程序目录不存在: " + programFolder);
            return 2;
        }

        EnsureRuntimeAssemblyResolver();

        var folder = Path.GetFullPath(programFolder);
        var meta = TryReadMeta(folder);
        var entryName = string.IsNullOrWhiteSpace(meta?.FileName) ? "main.cs" : meta!.FileName!.Trim();
        var entryPath = Path.Combine(folder, entryName);
        if (!File.Exists(entryPath))
        {
            Console.Error.WriteLine("入口不存在: " + entryPath);
            return 2;
        }

        BindSettingsFromDisk(folder);

        var folderName = new DirectoryInfo(folder).Name;
        Fly.BindLogContext(folderName, meta?.Name ?? folderName, folder);

        var source = File.ReadAllText(entryPath);
        source = ProgramScriptHost.PrepareSource(source);

        var extras = new List<(string Path, string Text)>();
        foreach (var rel in meta?.ExtraFiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var p = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) continue;
            extras.Add((rel.Replace('\\', '/'), File.ReadAllText(p)));
        }

        var extraRefs = LoadExtraReferences(folder, meta);
        Console.WriteLine($"[DevHost] 编译 {entryName}（extras={extras.Count}, refs={extraRefs.Count}）…");

        var compiled = ProgramScriptHost.Compile(
            source,
            emitAssembly: true,
            extras,
            extraRefs,
            entryPath: entryName.Replace('\\', '/'));

        if (!compiled.Success || compiled.AssemblyBytes is null)
        {
            var diags = ProgramScriptHost.MapDiagnostics(
                compiled.Diagnostics, entryName, compiled.LineOffset, compiled.EntryPath);
            foreach (var d in diags)
                Console.Error.WriteLine(d.FormatLine());
            Console.Error.WriteLine("[DevHost] 编译失败。");
            return 1;
        }

        Console.WriteLine("[DevHost] 运行中（Ctrl+C 停止）…");
        var gate = ProgramDebugGateNop.Instance;
        try
        {
            var probes = ProgramScriptHost.BuildRuntimeAssemblyProbePaths(folder);
            await ProgramScriptHost.RunCompiledAsync(compiled.AssemblyBytes, ct, gate, probes)
                .ConfigureAwait(false);
            Console.WriteLine("[DevHost] 已结束 (exit=0)");
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("[DevHost] 已取消");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[DevHost] 运行失败: " + ex.Message);
            return 1;
        }
        finally
        {
            try { Fly.ClearSettings(); } catch { /* ignore */ }
            try { Fly.ClearLogContext(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 裸 Reference <c>FlyScreen.Core</c> 时 deps.json 不含 Roslyn/Jint；
    /// 从宿主目录 / Core 旁 / 输出目录补解析（勿依赖 refs/ 整包拷贝）。
    /// </summary>
    public static void EnsureRuntimeAssemblyResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0)
            return;

        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
        {
            var simple = assemblyName.Name;
            if (string.IsNullOrEmpty(simple))
                return null;

            foreach (var dir in EnumerateRuntimeDependencyDirs())
            {
                try
                {
                    var path = Path.Combine(dir, simple + ".dll");
                    if (!File.Exists(path) || !ProgramScriptHost.HasManagedMetadata(path))
                        continue;
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                }
                catch
                {
                    // try next
                }
            }

            return null;
        };
    }

    private static IEnumerable<string> EnumerateRuntimeDependencyDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var full = Path.GetFullPath(dir);
                if (Directory.Exists(full))
                    seen.Add(full);
            }
            catch
            {
                // ignore
            }
        }

        Add(AppContext.BaseDirectory);
        try
        {
            var coreLoc = typeof(ProgramScriptHost).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(coreLoc))
                Add(Path.GetDirectoryName(coreLoc));
        }
        catch
        {
            // ignore
        }

        Add(ProgramExternalDevScaffold.TryFindHostDirectory());
        Add(Environment.GetEnvironmentVariable("FLYSCREEN_HOST_DIR"));

        return seen;
    }

    private static void BindSettingsFromDisk(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "settings.json");
            if (!File.Exists(path))
            {
                Fly.BindSettings(null);
                return;
            }

            var file = JsonSerializer.Deserialize<ProgramSettingsFile>(
                File.ReadAllText(path), JsonOpts);
            Fly.BindSettings(file?.Values);
            Console.WriteLine($"[DevHost] 已加载 settings.json（{file?.Values?.Count ?? 0} 项）");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[DevHost] 加载 settings.json 失败: " + ex.Message);
            Fly.BindSettings(null);
        }
    }

    private static DevMeta? TryReadMeta(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "program.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<DevMeta>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static List<Microsoft.CodeAnalysis.MetadataReference> LoadExtraReferences(
        string folder, DevMeta? meta)
    {
        var list = new List<Microsoft.CodeAnalysis.MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 本地 refs/*.dll + program.json LocalDlls
        void TryAddFile(string dll)
        {
            if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) return;
            var name = Path.GetFileNameWithoutExtension(dll);
            if (!seen.Add(name)) return;
            if (!ProgramScriptHost.TryCreateReferenceFromFile(dll, out var reference, out var error, out _))
            {
                // 原生/WinRT DLL 常见于误拷宿主目录到 refs/；跳过以免 CS0009
                Console.Error.WriteLine("[DevHost] " + (error ?? ("跳过引用: " + dll)));
                return;
            }
            list.Add(reference!);
        }

        var refsDir = Path.Combine(folder, "refs");
        if (Directory.Exists(refsDir))
        {
            foreach (var dll in Directory.EnumerateFiles(refsDir, "*.dll"))
                TryAddFile(dll);
        }

        foreach (var rel in meta?.LocalDlls ?? [])
        {
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var p = Path.IsPathRooted(rel) ? rel : Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            TryAddFile(p);
        }

        // NuGet restore 缓存（obj/fly-refs.txt）
        var cache = Path.Combine(folder, "obj", "fly-refs.txt");
        if (File.Exists(cache))
        {
            foreach (var line in File.ReadAllLines(cache))
            {
                var t = line.Trim();
                if (t.Length > 0) TryAddFile(t);
            }
        }

        return list;
    }

    private sealed class DevMeta
    {
        public string? Name { get; set; }
        public string? FileName { get; set; }
        public List<string>? ExtraFiles { get; set; }
        public List<string>? LocalDlls { get; set; }
    }
}
