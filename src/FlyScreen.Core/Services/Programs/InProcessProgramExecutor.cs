using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace FlyScreen.Services.Programs;

/// <summary>
/// 手机端进程内执行：JavaScript（Jint）与 C#（Roslyn Scripting）。
/// 不依赖系统安装的 node / dotnet。
/// </summary>
public static class InProcessProgramExecutor
{
    public static bool IsMobileHost
        => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    public static bool Supports(ProgramLanguage lang)
        => lang is ProgramLanguage.JavaScript or ProgramLanguage.CSharp;

    /// <summary>手机上对 JS/C# 优先走嵌入引擎。</summary>
    public static bool PreferInProcess(ProgramLanguage lang)
        => IsMobileHost && Supports(lang);

    public static async Task RunAsync(
        UserProgram prog,
        string source,
        CancellationToken ct,
        Action<string>? onInfo = null,
        Action<string>? onWarn = null)
    {
        onInfo ??= s => AppLog.Info(s, prog.Id, prog.Name);
        onWarn ??= s => AppLog.Warn(s, prog.Id, prog.Name);

        switch (prog.Language)
        {
            case ProgramLanguage.JavaScript:
                await RunJavaScriptAsync(prog, source, ct, onInfo, onWarn).ConfigureAwait(false);
                break;
            case ProgramLanguage.CSharp:
                await RunCSharpAsync(prog, source, ct, onInfo, onWarn).ConfigureAwait(false);
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
        Action<string> onInfo, Action<string> onWarn)
    {
        // 常见 await Task.Delay(n) 补上 ct，否则点停止无法打断延时
        var code = InjectCancellationIntoDelays(source ?? "");

        var options = ScriptOptions.Default
            .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)
            .AddReferences(
                typeof(object).Assembly,
                typeof(Console).Assembly,
                typeof(Enumerable).Assembly,
                typeof(Task).Assembly,
                typeof(StringBuilder).Assembly)
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Text",
                "System.Threading",
                "System.Threading.Tasks");

        await CSharpConsoleGate.WaitAsync(ct).ConfigureAwait(false);
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        try
        {
            Console.SetOut(new LineLogWriter(s => onInfo($"[{prog.Name}] {s}")));
            Console.SetError(new LineLogWriter(s => onWarn($"[{prog.Name}] {s}")));

            var globals = new CSharpScriptGlobals { CancellationToken = ct };
            var script = CSharpScript.Create(code, options, typeof(CSharpScriptGlobals));
            var state = await script.RunAsync(globals, cancellationToken: ct).ConfigureAwait(false);
            _ = state;
        }
        finally
        {
            try { Console.SetOut(prevOut); } catch { /* ignore */ }
            try { Console.SetError(prevErr); } catch { /* ignore */ }
            CSharpConsoleGate.Release();
        }
    }

    /// <summary>Task.Delay(x) → Task.Delay(x, ct)；已带 token 的不改。</summary>
    private static string InjectCancellationIntoDelays(string source)
    {
        return Regex.Replace(
            source,
            @"Task\s*\.\s*Delay\s*\(\s*(?<arg>[^,\)]+?)\s*\)",
            m => $"Task.Delay({m.Groups["arg"].Value.Trim()}, ct)",
            RegexOptions.CultureInvariant);
    }

    public sealed class CSharpScriptGlobals
    {
        public CancellationToken CancellationToken { get; init; }
        public CancellationToken ct => CancellationToken;

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
