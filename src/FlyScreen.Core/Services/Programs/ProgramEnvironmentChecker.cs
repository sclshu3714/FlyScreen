using System.Diagnostics;

namespace FlyScreen.Services.Programs;

public sealed class ProgramEnvCheckResult
{
    public bool Ok { get; init; }
    public string ToolName { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? InstallUrl { get; init; }
    public string? WingetId { get; init; }
    public string? BrewFormula { get; init; }
}

/// <summary>
/// 检测各语言运行/调试环境；缺失时可打开安装页或尝试 winget/brew 自动安装。
/// </summary>
public static class ProgramEnvironmentChecker
{
    public static ProgramEnvCheckResult Check(ProgramLanguage lang) => lang switch
    {
        ProgramLanguage.Python => CheckTool(
            display: "Python",
            commands: ["python", "python3", "py"],
            versionArgs: "--version",
            installUrl: "https://www.python.org/downloads/",
            wingetId: "Python.Python.3.12",
            brew: "python"),
        ProgramLanguage.CSharp => CheckTool(
            display: ".NET SDK",
            commands: ["dotnet"],
            versionArgs: "--list-sdks",
            installUrl: "https://dotnet.microsoft.com/download",
            wingetId: "Microsoft.DotNet.SDK.8",
            brew: "dotnet"),
        ProgramLanguage.JavaScript => CheckTool(
            display: "Node.js",
            commands: ["node", "nodejs"],
            versionArgs: "--version",
            installUrl: "https://nodejs.org/",
            wingetId: "OpenJS.NodeJS.LTS",
            brew: "node"),
        ProgramLanguage.PowerShell => CheckPowerShell(),
        ProgramLanguage.Bash => CheckBash(),
        ProgramLanguage.Batch => CheckBatch(),
        _ => new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = lang.ToString(),
            Detail = "暂不支持该语言的环境检测"
        }
    };

    public static void OpenInstallPage(ProgramEnvCheckResult result)
    {
        var url = result.InstallUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>Windows 用 winget，macOS 用 brew；其它平台返回失败并建议打开安装页。</summary>
    public static async Task<(bool Ok, string Message)> TryAutoInstallAsync(
        ProgramLanguage lang, ProgramEnvCheckResult result, CancellationToken ct = default)
    {
        if (result.Ok)
            return (true, "环境已就绪，无需安装");

        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(result.WingetId))
        {
            if (!ExistsOnPath("winget"))
                return (false, "未找到 winget，请打开安装页手动安装，或先安装「应用安装程序」。");

            var (ok, output) = await RunProcessAsync(
                "winget",
                $"install -e --id {result.WingetId} --accept-package-agreements --accept-source-agreements",
                180_000, ct).ConfigureAwait(false);
            if (ok)
            {
                var again = Check(lang);
                if (again.Ok)
                    return (true, $"{result.ToolName} 已安装并通过检测。");
                return (true, $"{result.ToolName} 安装命令已执行。请关闭并重新打开玄鸟后再点检测。\n{Trim(output, 400)}");
            }
            return (false, $"自动安装失败：\n{Trim(output, 500)}");
        }

        if (OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(result.BrewFormula) && ExistsOnPath("brew"))
        {
            var (ok, output) = await RunProcessAsync(
                "brew", $"install {result.BrewFormula}", 300_000, ct).ConfigureAwait(false);
            return ok
                ? (true, $"{result.ToolName} 已通过 Homebrew 安装。请重新检测。")
                : (false, $"brew 安装失败：\n{Trim(output, 500)}");
        }

        return (false, "当前系统不支持自动安装，请打开安装页手动安装。");
    }

    private static ProgramEnvCheckResult CheckTool(
        string display, string[] commands, string versionArgs,
        string installUrl, string? wingetId, string? brew)
    {
        foreach (var cmd in commands)
        {
            if (!ExistsOnPath(cmd) && !LooksLikeAbsoluteExists(cmd))
                continue;
            var (ok, output) = RunProcess(cmd, versionArgs, 4000);
            if (!ok && cmd.Equals("py", StringComparison.OrdinalIgnoreCase))
            {
                (ok, output) = RunProcess(cmd, "-3 --version", 4000);
            }
            if (ok || !string.IsNullOrWhiteSpace(output))
            {
                var ver = Trim(output, 120).Replace("\r", " ").Replace("\n", " ");
                return new ProgramEnvCheckResult
                {
                    Ok = true,
                    ToolName = display,
                    Detail = string.IsNullOrWhiteSpace(ver)
                        ? $"已找到 {cmd}，可用于运行与调试"
                        : $"已找到 {cmd}：{ver}",
                    InstallUrl = installUrl,
                    WingetId = wingetId,
                    BrewFormula = brew
                };
            }
        }

        return new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = display,
            Detail = $"未找到 {display}（尝试：{string.Join(", ", commands)}）。运行与调试需要安装该环境。",
            InstallUrl = installUrl,
            WingetId = wingetId,
            BrewFormula = brew
        };
    }

    private static ProgramEnvCheckResult CheckPowerShell()
    {
        if (ExistsOnPath("pwsh"))
        {
            var (_, output) = RunProcess("pwsh", "-NoProfile -Command \"$PSVersionTable.PSVersion\"", 4000);
            return new ProgramEnvCheckResult
            {
                Ok = true,
                ToolName = "PowerShell",
                Detail = string.IsNullOrWhiteSpace(output)
                    ? "已找到 pwsh（PowerShell 7+）"
                    : $"已找到 pwsh：{Trim(output, 80)}",
                InstallUrl = "https://aka.ms/powershell",
                WingetId = "Microsoft.PowerShell",
                BrewFormula = "powershell"
            };
        }

        if (OperatingSystem.IsWindows() && ExistsOnPath("powershell"))
        {
            return new ProgramEnvCheckResult
            {
                Ok = true,
                ToolName = "PowerShell",
                Detail = "已找到 Windows PowerShell，可用于运行脚本",
                InstallUrl = "https://aka.ms/powershell",
                WingetId = "Microsoft.PowerShell"
            };
        }

        return new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = "PowerShell",
            Detail = "未找到 pwsh / powershell",
            InstallUrl = "https://aka.ms/powershell",
            WingetId = "Microsoft.PowerShell",
            BrewFormula = "powershell"
        };
    }

    private static ProgramEnvCheckResult CheckBash()
    {
        if (OperatingSystem.IsWindows())
        {
            var gitBash = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Git", "bin", "bash.exe");
            if (File.Exists(gitBash))
            {
                return new ProgramEnvCheckResult
                {
                    Ok = true,
                    ToolName = "Bash",
                    Detail = $"已找到 Git Bash：{gitBash}",
                    InstallUrl = "https://git-scm.com/downloads",
                    WingetId = "Git.Git"
                };
            }
        }

        if (ExistsOnPath("bash"))
        {
            var (_, output) = RunProcess("bash", "--version", 4000);
            return new ProgramEnvCheckResult
            {
                Ok = true,
                ToolName = "Bash",
                Detail = string.IsNullOrWhiteSpace(output) ? "已找到 bash" : Trim(output, 100),
                InstallUrl = "https://git-scm.com/downloads",
                WingetId = "Git.Git",
                BrewFormula = "bash"
            };
        }

        return new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = "Bash",
            Detail = OperatingSystem.IsWindows()
                ? "未找到 bash。可安装 Git for Windows（含 Git Bash）。"
                : "未找到 bash",
            InstallUrl = "https://git-scm.com/downloads",
            WingetId = "Git.Git",
            BrewFormula = "bash"
        };
    }

    private static ProgramEnvCheckResult CheckBatch()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProgramEnvCheckResult
            {
                Ok = true,
                ToolName = "cmd",
                Detail = "Windows 自带 cmd.exe，Batch 脚本可直接运行",
                InstallUrl = null
            };
        }

        return new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = "cmd",
            Detail = "Batch（.bat）仅适用于 Windows。当前系统请改用 Bash / Python 等。",
            InstallUrl = null
        };
    }

    private static bool LooksLikeAbsoluteExists(string command)
        => command.Contains('\\') || command.Contains('/')
           ? File.Exists(command)
           : false;

    private static bool ExistsOnPath(string command)
    {
        try
        {
            if (OperatingSystem.IsWindows() && (command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || command.Contains('\\')))
                return File.Exists(command);

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
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (bool Ok, string Output) RunProcess(string fileName, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "超时");
            }
            var output = (stdout + "\n" + stderr).Trim();
            return (p.ExitCode == 0 || output.Length > 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(bool Ok, string Output)> RunProcessAsync(
        string fileName, string args, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "无法启动进程");
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "安装超时");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var output = (stdout + "\n" + stderr).Trim();
            return (p.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
