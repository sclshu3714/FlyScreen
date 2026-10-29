using System.Text;
using Jint;
using Microsoft.CodeAnalysis.CSharp.Scripting;

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
        ProgramDebugGate? debugGate = null)
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

        switch (prog.Language)
        {
            case ProgramLanguage.JavaScript:
                await RunJavaScriptAsync(prog, source, ct, onInfo, onWarn).ConfigureAwait(false);
                break;
            case ProgramLanguage.CSharp:
                await RunCSharpAsync(prog, source, ct, onInfo, onWarn, debug, debugGate).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"进程内执行不支持：{prog.LanguageText}");
        }
    }

    private static Task RunJavaScriptAsync(
        UserProgram prog, string source, CancellationToken ct,
        Action<string> onInfo, Action<string> onWarn)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var engine = new Engine(options =>
            {
                options.LimitMemory(64_000_000);
                options.TimeoutInterval(TimeSpan.FromHours(2));
                options.MaxStatements(10_000_000);
                options.CancellationToken(ct);
                options.Strict = false;
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

            engine.Execute(source ?? "");
        }, ct);
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
        bool debug, ProgramDebugGate? debugGate)
    {
        var code = ProgramScriptHost.PrepareSource(source ?? "");
        if (debug)
        {
            var bps = prog.Breakpoints ?? [];
            code = ProgramDebugInstrumenter.Instrument(code, bps);
            onInfo($"调试「{prog.Name}」：断点 {bps.Count} 处已注入协作式暂停");
        }

        var (wrapped, _) = ProgramScriptHost.WrapAsEntry(code);
        var options = ProgramScriptHost.CreateScriptOptions();
        var gate = debugGate ?? (debug ? new ProgramDebugGate() : null);
        gate?.SetBreakpoints(prog.Breakpoints ?? []);

        await CSharpConsoleGate.WaitAsync(ct).ConfigureAwait(false);
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        try
        {
            Console.SetOut(new LineLogWriter(s => onInfo($"[{prog.Name}] {s}")));
            Console.SetError(new LineLogWriter(s => onWarn($"[{prog.Name}] {s}")));

            gate?.BeginRun();
            // 无 globalsType，避免单文件宿主 Location 为空导致元数据引用失败
            var script = CSharpScript.Create(wrapped, options);
            var state = await script.RunAsync(cancellationToken: ct).ConfigureAwait(false);
            if (state.ReturnValue is Func<CancellationToken, ProgramDebugGate, Task> entry)
                await entry(ct, gate ?? ProgramDebugGateNop.Instance).ConfigureAwait(false);
            else if (state.ReturnValue is Task t)
                await t.ConfigureAwait(false);
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
