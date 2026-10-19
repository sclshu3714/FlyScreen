using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace FlyScreen.Services.Programs;

/// <summary>后台启动 / 停止用户程序，stdout/stderr 写入 AppLog。</summary>
public sealed class ProgramRunner
{
    private readonly ProgramCatalog _catalog;
    private readonly ConcurrentDictionary<string, RunningProcess> _running = new();

    public ProgramRunner(ProgramCatalog catalog)
    {
        _catalog = catalog;
    }

    public bool IsRunning(string programId)
        => _running.TryGetValue(programId, out var r) && r.Process is { HasExited: false };

    public ProgramStatus GetStatus(string programId)
    {
        if (!_running.TryGetValue(programId, out var r))
            return ProgramStatus.Idle;
        if (r.Process is { HasExited: false })
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

        var source = _catalog.GetSourcePath(prog);
        if (!File.Exists(source))
            throw new FileNotFoundException("程序文件不存在", source);

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

        var entry = new RunningProcess(process, debug);
        _running[prog.Id] = entry;
        prog.RuntimeStatus = ProgramStatus.Running;
        AppLog.Info(debug
            ? $"调试启动「{prog.Name}」({prog.LanguageText})"
            : $"已启动「{prog.Name}」({prog.LanguageText})", prog.Id, prog.Name);

        _ = Task.Run(() => PumpOutputAsync(prog, entry), CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
        _ = ct;
    }

    public async Task StopAsync(UserProgram prog)
    {
        if (!_running.TryGetValue(prog.Id, out var entry) || entry.Process.HasExited)
        {
            prog.RuntimeStatus = ProgramStatus.Idle;
            _running.TryRemove(prog.Id, out _);
            return;
        }

        entry.Stopping = true;
        prog.RuntimeStatus = ProgramStatus.Stopping;
        AppLog.Info($"正在停止「{prog.Name}」…", prog.Id, prog.Name);

        try
        {
            entry.Process.Kill(entireProcessTree: true);
            await entry.Process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"停止「{prog.Name}」: {ex.Message}", prog.Id, prog.Name);
        }
        finally
        {
            _running.TryRemove(prog.Id, out _);
            prog.RuntimeStatus = ProgramStatus.Idle;
            AppLog.Info($"已停止「{prog.Name}」", prog.Id, prog.Name);
            try { entry.Process.Dispose(); } catch { /* ignore */ }
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
        if (IsRunning(prog.Id))
            await StopAsync(prog).ConfigureAwait(false);
        _catalog.Delete(prog);
    }

    public async Task DeleteManyAsync(IEnumerable<UserProgram> programs)
    {
        foreach (var p in programs.ToList())
        {
            try { await DeleteAsync(p).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Error(ex, $"删除「{p.Name}」失败", p.Id, p.Name); }
        }
    }

    private async Task PumpOutputAsync(UserProgram prog, RunningProcess entry)
    {
        var p = entry.Process;
        try
        {
            var outTask = ReadLinesAsync(p.StandardOutput, line =>
                AppLog.Info($"[{prog.Name}] {line}", prog.Id, prog.Name));
            var errTask = ReadLinesAsync(p.StandardError, line =>
                AppLog.Warn($"[{prog.Name}] {line}", prog.Id, prog.Name));
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            entry.ExitCode = p.ExitCode;
            if (!entry.Stopping)
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
            prog.RuntimeStatus = entry.ExitCode is null or 0
                ? ProgramStatus.Idle
                : ProgramStatus.Error;
            try { p.Dispose(); } catch { /* ignore */ }
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
                    ? $"-u {quoted}" // 断点调试预留：当前仍直接运行；编辑器可写 pdb
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

    private sealed class RunningProcess(Process process, bool debug)
    {
        public Process Process { get; } = process;
        public bool Debug { get; } = debug;
        public bool Stopping { get; set; }
        public int? ExitCode { get; set; }
    }
}
