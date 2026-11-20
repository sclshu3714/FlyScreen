using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Microsoft.CodeAnalysis;

namespace FlyScreen.Services.Programs;

/// <summary>
/// 进程内执行：C#（Roslyn，全端）与 JavaScript（Jint，手机端）。
/// C# 统一走脚本引擎，才能使用 <see cref="Fly"/> / <c>ct</c> / <c>Delay</c>；
/// 电脑上 <see cref="Fly"/> 无设备实现时 <see cref="Fly.Ready"/> 为 false。
/// </summary>
public static class InProcessProgramExecutor
{
    public static bool IsMobileHost
        => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    public static bool Supports(ProgramLanguage lang)
        => lang is ProgramLanguage.JavaScript or ProgramLanguage.CSharp;

    /// <summary>
    /// C# 全端进程内（含 Fly API）；JS 仅手机进程内（电脑仍可用系统 node）。
    /// </summary>
    public static bool PreferInProcess(ProgramLanguage lang)
        => lang == ProgramLanguage.CSharp
           || (IsMobileHost && lang == ProgramLanguage.JavaScript);

    public static async Task RunAsync(
        UserProgram prog,
        string source,
        CancellationToken ct,
        Action<string>? onInfo = null,
        Action<string>? onWarn = null,
        bool debug = false,
        ProgramDebugGate? debugGate = null,
        ProgramCatalog? catalog = null)
    {
        // Console.WriteLine → Info；失败类文案提升为 Warn（仍受程序 logLevel 过滤）
        onInfo ??= s =>
        {
            if (LooksLikeScriptWarn(s))
                AppLog.Warn(s, prog.Id, prog.Name);
            else
                AppLog.Info(s, prog.Id, prog.Name);
        };
        onWarn ??= s => AppLog.Warn(s, prog.Id, prog.Name);

        catalog ??= ProgramService.Instance.Catalog;

        switch (prog.Language)
        {
            case ProgramLanguage.JavaScript:
                await RunJavaScriptAsync(prog, source, ct, onInfo, onWarn, catalog).ConfigureAwait(false);
                break;
            case ProgramLanguage.CSharp:
                await RunCSharpAsync(prog, source, ct, onInfo, onWarn, debug, debugGate, catalog).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"进程内执行不支持：{prog.LanguageText}");
        }
    }

    private static Task RunJavaScriptAsync(
        UserProgram prog, string source, CancellationToken ct,
        Action<string> onInfo, Action<string> onWarn, ProgramCatalog catalog)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var folder = catalog.GetFolder(prog);
            var useModules = LooksLikeEsModule(source) || catalog.ReadExtraSources(prog).Count > 0;

            var engine = new Engine(options =>
            {
                options.LimitMemory(64_000_000);
                options.TimeoutInterval(TimeSpan.FromHours(2));
                options.MaxStatements(10_000_000);
                options.CancellationToken(ct);
                options.Strict = false;
                if (useModules)
                    options.EnableModules(folder);
            });

            engine.SetValue("__flyLog", new Action<string>(s => onInfo($"[{prog.Name}] {s}")));
            engine.SetValue("__flyWarn", new Action<string>(s => onWarn($"[{prog.Name}] {s}")));
            engine.SetValue("Fly", new FlyScriptBridge());
            // 可取消的 sleep(ms)，便于循环脚本被「停止」打断
            engine.SetValue("sleep", new Action<int>(ms =>
            {
                var end = Environment.TickCount64 + Math.Max(0, ms);
                while (Environment.TickCount64 < end)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(Math.Min(50, (int)Math.Max(1, end - Environment.TickCount64)));
                }
            }));
            engine.Execute("""
                var console = {
                  log: function() { __flyLog(Array.prototype.slice.call(arguments).join(' ')); },
                  info: function() { __flyLog(Array.prototype.slice.call(arguments).join(' ')); },
                  warn: function() { __flyWarn(Array.prototype.slice.call(arguments).join(' ')); },
                  error: function() { __flyWarn(Array.prototype.slice.call(arguments).join(' ')); }
                };
                """);

            if (useModules)
            {
                // 确保磁盘上的入口与编辑缓冲一致（Runner 已读 source；写回避免 Import 读到旧文件）
                try
                {
                    var entryPath = catalog.GetSourcePath(prog);
                    Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
                    File.WriteAllText(entryPath, source ?? "", new UTF8Encoding(false));
                }
                catch
                {
                    // 内置只读等：仍尝试 Import 磁盘文件
                }

                var spec = "./" + (prog.FileName ?? "main.js").Replace('\\', '/');
                engine.Modules.Import(spec);
            }
            else
            {
                engine.Execute(source ?? "");
            }
        }, ct);
    }

    private static bool LooksLikeEsModule(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        return Regex.IsMatch(
            source,
            @"(^|\n)\s*(import\s|export\s)",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeScriptWarn(string? s)
        => !string.IsNullOrEmpty(s)
           && (s.Contains("执行失败", StringComparison.Ordinal)
               || s.Contains("失败", StringComparison.Ordinal)
               || s.Contains("错误", StringComparison.Ordinal));

    private static readonly SemaphoreSlim CSharpConsoleGate = new(1, 1);

    private static async Task RunCSharpAsync(
        UserProgram prog, string source, CancellationToken ct,
        Action<string> onInfo, Action<string> onWarn,
        bool debug, ProgramDebugGate? debugGate, ProgramCatalog catalog)
    {
        catalog.NormalizeManifest(prog);
        if (prog.UiKind != ProgramUiKind.None
            && !ProgramUiCompatibility.CanRunLocal(prog.UiKind))
        {
            throw new InvalidOperationException(
                $"程序「{prog.Name}」需要 {ProgramUiCompatibility.DisplayName(prog.UiKind)} UI，" +
                $"本机宿主（{ProgramUiCompatibility.LocalHostKind}）不兼容。");
        }

        var code = ProgramScriptHost.PrepareSource(source ?? "");
        if (debug)
        {
            var bps = prog.Breakpoints ?? [];
            code = ProgramDebugInstrumenter.Instrument(code, bps);
            onInfo($"调试「{prog.Name}」：断点 {bps.Count} 处已注入协作式暂停");
        }

        var gate = debugGate ?? (debug ? new ProgramDebugGate() : null);
        gate?.SetBreakpoints(prog.Breakpoints ?? []);

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

            if (nonManaged)
            {
                AppLog.Warn("脚本运行: " + error);
                continue;
            }

            throw new InvalidOperationException(
                $"本地 DLL 引用失败「{Path.GetFileName(dll)}」: {error}");
        }

        if (prog.Packages.Count > 0)
        {
            if (ProgramNugetResolver.CanRestoreOnline)
            {
                var nuget = await ProgramNugetResolver.ResolveAsync(catalog, prog, ct).ConfigureAwait(false);
                if (!nuget.Success)
                    throw new InvalidOperationException(nuget.Error ?? "NuGet 还原失败");
                foreach (var path in nuget.ReferencePaths)
                {
                    if (ProgramScriptHost.TryCreateReferenceFromFile(path, out var reference, out var error, out var nonManaged))
                    {
                        refs.Add(reference!);
                        continue;
                    }

                    if (nonManaged)
                    {
                        AppLog.Warn("脚本运行: " + error);
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"NuGet 引用失败「{Path.GetFileName(path)}」: {error}");
                }
            }
            else if (catalog.ResolveLocalDllPaths(prog).Count == 0
                     && ProgramNugetResolver.TryReadRefsCache(catalog, prog).Count == 0)
            {
                throw new InvalidOperationException(
                    "程序声明了 NuGet 包，但手机端不能在线还原。请把依赖 DLL 放入 refs/ 并在程序依赖中登记。");
            }
            else
            {
                foreach (var path in ProgramNugetResolver.TryReadRefsCache(catalog, prog))
                {
                    if (ProgramScriptHost.TryCreateReferenceFromFile(path, out var reference, out _, out _))
                        refs.Add(reference!);
                }
            }
        }

        var entryName = string.IsNullOrWhiteSpace(prog.FileName) ? "main.cs" : prog.FileName;
        var compiled = ProgramScriptHost.Compile(
            code, emitAssembly: true, extras, refs, entryPath: entryName);
        if (!compiled.Success || compiled.AssemblyBytes is null)
        {
            var diags = ProgramScriptHost.MapDiagnostics(
                compiled.Diagnostics, entryName, compiled.LineOffset, compiled.EntryPath);
            var report = string.Join(Environment.NewLine, diags.Select(d => d.FormatLine()));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(report)
                    ? "C# 脚本编译失败。"
                    : report);
        }

        await CSharpConsoleGate.WaitAsync(ct).ConfigureAwait(false);
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        try
        {
            Console.SetOut(new LineLogWriter(s => onInfo($"[{prog.Name}] {s}")));
            Console.SetError(new LineLogWriter(s => onWarn($"[{prog.Name}] {s}")));

            gate?.BeginRun();
            var probes = ProgramScriptHost.BuildRuntimeAssemblyProbePaths(catalog.GetFolder(prog));
            await ProgramScriptHost.RunCompiledAsync(
                compiled.AssemblyBytes,
                ct,
                gate ?? ProgramDebugGateNop.Instance,
                probes).ConfigureAwait(false);
        }
        finally
        {
            gate?.EndRun();
            try { Console.SetOut(prevOut); } catch { /* ignore */ }
            try { Console.SetError(prevErr); } catch { /* ignore */ }
            CSharpConsoleGate.Release();
        }
    }

    /// <summary>保留类型供旧代码/文档引用；运行路径已改为 <see cref="ProgramScriptHost.WrapAsEntry"/>。</summary>
    public sealed class CSharpScriptGlobals
    {
        public CancellationToken CancellationToken { get; init; }
        public CancellationToken ct => CancellationToken;
        public ProgramDebugGate __flyDbg { get; set; } = ProgramDebugGateNop.Instance;
        public Task Delay(int milliseconds) => Task.Delay(milliseconds, CancellationToken);
    }

    private sealed class LineLogWriter : TextWriter
    {
        private readonly Action<string> _write;
        private readonly StringBuilder _buf = new();

        public LineLogWriter(Action<string> write) => _write = write;
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value is '\n' or '\r')
                FlushLine();
            else
                _buf.Append(value);
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (var ch in value)
                Write(ch);
        }

        public override void Flush() => FlushLine();

        private void FlushLine()
        {
            if (_buf.Length == 0) return;
            var line = _buf.ToString();
            _buf.Clear();
            if (line.Length > 0)
                _write(line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) FlushLine();
            base.Dispose(disposing);
        }
    }
}

/// <summary>非调试运行：Hit/Poll 立即返回。</summary>
public sealed class ProgramDebugGateNop : ProgramDebugGate
{
    public static ProgramDebugGateNop Instance { get; } = new();

    private ProgramDebugGateNop() { }

    public override Task HitAsync(int line, params (string Name, object? Value)[] locals)
        => Task.CompletedTask;

    public override Task LineAsync(int line, params (string Name, object? Value)[] locals)
        => Task.CompletedTask;

    public override Task PollAsync() => Task.CompletedTask;
}
