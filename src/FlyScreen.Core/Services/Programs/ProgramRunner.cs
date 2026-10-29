using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace FlyScreen.Services.Programs;

/// <summary>后台启动 / 停止用户程序，stdout/stderr 写入 AppLog。</summary>
public sealed class ProgramRunner
{
    private readonly ProgramCatalog _catalog;
    private readonly ConcurrentDictionary<string, RunningJob> _running = new();

    public ProgramRunner(ProgramCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>正在运行的程序数量变化（含 0）；Android 等宿主可据此启停保活。</summary>
    public event Action<int>? RunningCountChanged;

    public int RunningCount
    {
        get
        {
            var n = 0;
            foreach (var kv in _running)
            {
                if (kv.Value.IsAlive) n++;
            }
            return n;
        }
    }

    private void NotifyRunningCountChanged()
    {
        try { RunningCountChanged?.Invoke(RunningCount); }
        catch { /* ignore host hooks */ }
    }

    public bool IsRunning(string programId)
        => _running.TryGetValue(programId, out var r) && r.IsAlive;

    public ProgramStatus GetStatus(string programId)
    {
        if (!_running.TryGetValue(programId, out var r))
            return ProgramStatus.Idle;
        if (r.IsAlive)
            return r.Stopping ? ProgramStatus.Stopping : ProgramStatus.Running;
        return r.ExitCode is null or 0 ? ProgramStatus.Idle : ProgramStatus.Error;
    }

    public void SyncStatus(UserProgram prog)
        => prog.RuntimeStatus = GetStatus(prog.Id);

    public async Task StartAsync(UserProgram prog, bool debug = false, CancellationToken ct = default)
    {
        if (IsRunning(prog.Id))
        {
            AppLog.Info($"程序「{prog.Name}」已在运行", prog.Id, prog.Name);
            return;
        }

        var sourcePath = _catalog.GetSourcePath(prog);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("程序文件不存在", sourcePath);

        // 手机端 JS / C#：进程内引擎（不依赖系统 node / dotnet）
        if (InProcessProgramExecutor.PreferInProcess(prog.Language))
        {
            await StartInProcessAsync(prog, debug, ct).ConfigureAwait(false);
            return;
        }

        // 手机上其余语言无法用桌面 CLI
        if (InProcessProgramExecutor.IsMobileHost)
        {
            prog.RuntimeStatus = ProgramStatus.Error;
            var msg = $"手机端暂不支持运行「{prog.LanguageText}」。请改用 JavaScript 或 C#，或在电脑版玄鸟上运行。";
            AppLog.Error(msg, prog.Id, prog.Name);
            throw new NotSupportedException(msg);
        }

        await StartProcessAsync(prog, sourcePath, debug, ct).ConfigureAwait(false);
    }

    private Task StartInProcessAsync(UserProgram prog, bool debug, CancellationToken ct)
    {
        var source = _catalog.ReadSource(prog);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var job = RunningJob.InProcess(cts, debug);
        if (!_running.TryAdd(prog.Id, job))
        {
            cts.Dispose();
            AppLog.Info($"程序「{prog.Name}」已在运行", prog.Id, prog.Name);
            return Task.CompletedTask;
        }

        prog.RuntimeStatus = ProgramStatus.Running;
        AppLog.Info(debug
            ? $"调试启动「{prog.Name}」({prog.LanguageText} · 本机引擎)"
            : $"已启动「{prog.Name}」({prog.LanguageText} · 本机引擎)", prog.Id, prog.Name);
        NotifyRunningCountChanged();

        ProgramDebugGate? gate = null;
        if (debug && prog.Language == ProgramLanguage.CSharp)
        {
            gate = new ProgramDebugGate();
            ProgramDebugGate.Current = gate;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                try
                {
                    var settings = ProgramSettingsStore.LoadResolved(_catalog, prog);
                    Fly.BindSettings(settings);
                    Fly.BindLogContext(prog.Id, prog.Name);
                    var logLevel = ProgramSettingsSchema.ResolveLogLevel(settings);
                    // 先宣告再设过滤：带 programId 的 Info 在 Error 级下会被丢掉
                    AppLog.Info($"「{prog.Name}」日志级别={logLevel}（低于此级的脚本日志不写入）");
                    AppLog.SetProgramMinWriteLevel(logLevel);
                    var op = settings.GetValueOrDefault(ProgramRegionKeys.OcrPortrait) ?? "";
                    var ol = settings.GetValueOrDefault(ProgramRegionKeys.OcrLandscape) ?? "";
                    var ap = settings.GetValueOrDefault(ProgramRegionKeys.ActionPortrait) ?? "";
                    var al = settings.GetValueOrDefault(ProgramRegionKeys.ActionLandscape) ?? "";
                    if (!string.IsNullOrWhiteSpace(op) || !string.IsNullOrWhiteSpace(ol)
                        || !string.IsNullOrWhiteSpace(ap) || !string.IsNullOrWhiteSpace(al))
                        AppLog.Info(
                            $"「{prog.Name}」区域 竖识={op} 竖操={ap} 横识={ol} 横操={al}",
                            prog.Id, prog.Name);
                    // 调试浮层仅由 debugLog 控制（与日志写入级别 logLevel 分离）
                    if (ProgramSettingsSchema.AsBool(settings.GetValueOrDefault("debugLog"), false))
                        ProgramDebugHud.Start(prog.Id, prog.Name);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"加载程序设置失败「{prog.Name}」: {ex.Message}", prog.Id, prog.Name);
                    Fly.ClearSettings();
                    Fly.ClearLogContext();
                    AppLog.ClearProgramMinWriteLevel();
                }

                await InProcessProgramExecutor.RunAsync(
                    prog, source, cts.Token,
                    debug: debug,
                    debugGate: gate).ConfigureAwait(false);
                job.ExitCode = 0;
                if (!job.Stopping)
                    AppLog.Info($"「{prog.Name}」已结束 (exit=0)", prog.Id, prog.Name);
            }
            catch (OperationCanceledException) when (job.Stopping || cts.IsCancellationRequested)
            {
                job.ExitCode = 0; // 用户停止视为正常结束
            }
            catch (Exception ex)
            {
                if (job.Stopping || cts.IsCancellationRequested)
                    job.ExitCode = 0;
                else
                {
                    job.ExitCode = 1;
                    AppLog.Error(ex, $"「{prog.Name}」运行失败", prog.Id, prog.Name);
                }
            }
            finally
            {
                try { ProgramDebugHud.Stop(); } catch { /* ignore */ }
                try { Fly.ClearSettings(); } catch { /* ignore */ }
                try { Fly.ClearLogContext(); } catch { /* ignore */ }
                try { AppLog.ClearProgramMinWriteLevel(); } catch { /* ignore */ }
                try { gate?.EndRun(); } catch { /* ignore */ }
                if (ProgramDebugGate.Current == gate)
                    ProgramDebugGate.Current = null;
                job.Completed = true;
                _running.TryRemove(prog.Id, out _);
                // 强制停止时 StopAsync 已把状态置 Idle，避免再写成「错误」
                if (!job.ForceStopped)
                {
                    prog.RuntimeStatus = job.ExitCode is null or 0
                        ? ProgramStatus.Idle
                        : ProgramStatus.Error;
                }
                try { cts.Dispose(); } catch { /* ignore */ }
                NotifyRunningCountChanged();
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task StartProcessAsync(UserProgram prog, string source, bool debug, CancellationToken ct)
    {
        var (fileName, args, workDir) = BuildLaunch(prog, source, debug);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            prog.RuntimeStatus = ProgramStatus.Error;
            AppLog.Error(ex, $"启动「{prog.Name}」失败（请确认已安装 {prog.LanguageText} 运行时）", prog.Id, prog.Name);
            throw;
        }

        if (process == null)
        {
            prog.RuntimeStatus = ProgramStatus.Error;
            AppLog.Error($"启动「{prog.Name}」失败：无法创建进程", prog.Id, prog.Name);
            return;
        }

        var job = RunningJob.FromProcess(process, debug);
        _running[prog.Id] = job;
        prog.RuntimeStatus = ProgramStatus.Running;
        AppLog.Info(debug
            ? $"调试启动「{prog.Name}」({prog.LanguageText})"
            : $"已启动「{prog.Name}」({prog.LanguageText})", prog.Id, prog.Name);
        NotifyRunningCountChanged();

        _ = Task.Run(() => PumpOutputAsync(prog, job), CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
        _ = ct;
    }

    public async Task StopAsync(UserProgram prog)
    {
        if (!_running.TryGetValue(prog.Id, out var job) || !job.IsAlive)
        {
            prog.RuntimeStatus = ProgramStatus.Idle;
            _running.TryRemove(prog.Id, out _);
            NotifyRunningCountChanged();
            return;
        }

        job.Stopping = true;
        prog.RuntimeStatus = ProgramStatus.Stopping;
        AppLog.Info($"正在停止「{prog.Name}」…", prog.Id, prog.Name);

        // 协作式调试：先放开闸门，再取消，避免 Hit/Poll 永久阻塞
        try { ProgramDebugGate.Current?.EndRun(); } catch { /* ignore */ }

        try
        {
            if (job.Process != null)
            {
                job.Process.Kill(entireProcessTree: true);
                await job.Process.WaitForExitAsync().ConfigureAwait(false);
            }
            else
            {
                try { job.Cancel?.Cancel(); } catch { /* ignore */ }
                // 等待脚本响应取消（含 Task.Delay(..., ct)）
                var sw = Stopwatch.StartNew();
                while (IsRunning(prog.Id) && sw.Elapsed < TimeSpan.FromSeconds(2))
                    await Task.Delay(40).ConfigureAwait(false);

                // 仍未退出（如未使用 ct 的 Delay / 死循环）：强制结束「停止中」
                if (IsRunning(prog.Id))
                {
                    job.ForceStopped = true;
                    job.Completed = true;
                    job.ExitCode = 0;
                    _running.TryRemove(prog.Id, out _);
                    AppLog.Warn($"「{prog.Name}」未及时响应取消，已强制结束状态", prog.Id, prog.Name);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"停止「{prog.Name}」: {ex.Message}", prog.Id, prog.Name);
        }
        finally
        {
            // 无论进程还是本机引擎，停止调用返回后状态必须离开「停止中」
            prog.RuntimeStatus = ProgramStatus.Idle;
            if (job.Process != null)
            {
                _running.TryRemove(prog.Id, out _);
                try { job.Process.Dispose(); } catch { /* ignore */ }
            }
            AppLog.Info($"已停止「{prog.Name}」", prog.Id, prog.Name);
            NotifyRunningCountChanged();
        }
    }

    public async Task StartManyAsync(IEnumerable<UserProgram> programs, bool debug = false)
    {
        foreach (var p in programs)
        {
            try { await StartAsync(p, debug).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Error(ex, $"启动「{p.Name}」失败", p.Id, p.Name); }
        }
    }

    public async Task StopManyAsync(IEnumerable<UserProgram> programs)
    {
        foreach (var p in programs)
        {
            try { await StopAsync(p).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Error(ex, $"停止「{p.Name}」失败", p.Id, p.Name); }
        }
    }

    public async Task DeleteAsync(UserProgram prog)
    {
        if (prog.IsBuiltin
            || string.Equals(prog.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"「{prog.Name}」为内置程序，不可删除");

        if (IsRunning(prog.Id))
            await StopAsync(prog).ConfigureAwait(false);
        _catalog.Delete(prog);
    }

    public async Task DeleteManyAsync(IEnumerable<UserProgram> programs)
    {
        foreach (var p in programs.ToList())
        {
            if (p.IsBuiltin
                || string.Equals(p.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn($"已跳过删除内置程序「{p.Name}」", p.Id, p.Name);
                continue;
            }
            try { await DeleteAsync(p).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Error(ex, $"删除「{p.Name}」失败", p.Id, p.Name); }
        }
    }

    private async Task PumpOutputAsync(UserProgram prog, RunningJob job)
    {
        var p = job.Process!;
        try
        {
            var outTask = ReadLinesAsync(p.StandardOutput, line =>
                AppLog.Info($"[{prog.Name}] {line}", prog.Id, prog.Name));
            var errTask = ReadLinesAsync(p.StandardError, line =>
                AppLog.Warn($"[{prog.Name}] {line}", prog.Id, prog.Name));
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            job.ExitCode = p.ExitCode;
            if (!job.Stopping)
            {
                if (p.ExitCode == 0)
                    AppLog.Info($"「{prog.Name}」已结束 (exit={p.ExitCode})", prog.Id, prog.Name);
                else
                    AppLog.Error($"「{prog.Name}」异常结束 (exit={p.ExitCode})", prog.Id, prog.Name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"「{prog.Name}」输出读取: {ex.Message}", prog.Id, prog.Name);
        }
        finally
        {
            _running.TryRemove(prog.Id, out _);
            prog.RuntimeStatus = job.ExitCode is null or 0
                ? ProgramStatus.Idle
                : ProgramStatus.Error;
            try { p.Dispose(); } catch { /* ignore */ }
            NotifyRunningCountChanged();
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null) break;
            if (line.Length == 0) continue;
            try { onLine(line); } catch { /* ignore */ }
        }
    }

    private (string fileName, string args, string workDir) BuildLaunch(
        UserProgram prog, string sourcePath, bool debug)
    {
        var folder = _catalog.GetFolder(prog);
        var quoted = Quote(sourcePath);

        return prog.Language switch
        {
            ProgramLanguage.Python => (
                ResolvePython(),
                debug && prog.Breakpoints.Count > 0
                    ? $"-u {quoted}"
                    : $"-u {quoted}",
                folder),
            ProgramLanguage.CSharp => (
                "dotnet",
                $"run --project {Quote(Path.Combine(folder, "program.csproj"))} -c Release --no-launch-profile",
                folder),
            ProgramLanguage.JavaScript => (
                ResolveNode(),
                quoted,
                folder),
            ProgramLanguage.PowerShell => (
                ResolvePowerShell(),
                $"-NoProfile -ExecutionPolicy Bypass -File {quoted}",
                folder),
            ProgramLanguage.Bash => (
                ResolveBash(),
                quoted,
                folder),
            ProgramLanguage.Batch => (
                "cmd.exe",
                $"/c {quoted}",
                folder),
            _ => throw new NotSupportedException($"暂不支持语言: {prog.Language}")
        };
    }

    private static string ResolvePython()
    {
        foreach (var c in new[] { "python", "python3", "py" })
            if (ExistsOnPath(c)) return c;
        return "python";
    }

    private static string ResolveNode()
    {
        foreach (var c in new[] { "node", "nodejs" })
            if (ExistsOnPath(c)) return c;
        return "node";
    }

    private static string ResolvePowerShell()
    {
        if (ExistsOnPath("pwsh")) return "pwsh";
        return OperatingSystem.IsWindows() ? "powershell" : "pwsh";
    }

    private static string ResolveBash()
    {
        if (OperatingSystem.IsWindows())
        {
            var gitBash = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Git", "bin", "bash.exe");
            if (File.Exists(gitBash)) return gitBash;
        }
        return "bash";
    }

    private static bool ExistsOnPath(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(1500);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string path)
        => path.Contains(' ') ? $"\"{path}\"" : path;

    private sealed class RunningJob
    {
        public Process? Process { get; private init; }
        public CancellationTokenSource? Cancel { get; private init; }
        public bool Debug { get; private init; }
        public bool Stopping { get; set; }
        public bool ForceStopped { get; set; }
        public int? ExitCode { get; set; }
        public bool Completed { get; set; }

        public bool IsAlive
            => Process != null ? !Process.HasExited : !Completed;

        public static RunningJob FromProcess(Process process, bool debug) => new()
        {
            Process = process,
            Debug = debug
        };

        public static RunningJob InProcess(CancellationTokenSource cts, bool debug) => new()
        {
            Cancel = cts,
            Debug = debug
        };
    }
}
