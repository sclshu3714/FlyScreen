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
        onInfo ??= s => AppLog.Info(s, prog.Id, prog.Name);
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
            var globals = new CSharpScriptGlobals
            {
                CancellationToken = ct,
                __flyDbg = gate ?? ProgramDebugGateNop.Instance
            };
            var script = CSharpScript.Create(code, options, typeof(CSharpScriptGlobals));
            var state = await script.RunAsync(globals, cancellationToken: ct).ConfigureAwait(false);
            _ = state;
        }
        finally
        {
            gate?.EndRun();
            try { Console.SetOut(prevOut); } catch { /* ignore */ }
            try { Console.SetError(prevErr); } catch { /* ignore */ }
            CSharpConsoleGate.Release();
        }
    }

    public sealed class CSharpScriptGlobals
    {
        public CancellationToken CancellationToken { get; init; }
        public CancellationToken ct => CancellationToken;

        /// <summary>调试闸门（非调试时为 Nop）。</summary>
        public ProgramDebugGate __flyDbg { get; set; } = ProgramDebugGateNop.Instance;

        /// <summary>可取消延时，脚本可写 await Delay(1000)。</summary>
        public Task Delay(int milliseconds)
            => Task.Delay(milliseconds, CancellationToken);
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
