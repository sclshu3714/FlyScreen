using System.Diagnostics;
using System.Text;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.Services.Programs;

public sealed class ProgramEnvCheckResult
{
    public bool Ok { get; init; }
    public string ToolName { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? InstallUrl { get; init; }
    public string? WingetId { get; init; }
    public string? BrewFormula { get; init; }
    /// <summary>Debian/Ubuntu apt 包名（可能需额外源）。</summary>
    public string? AptPackage { get; init; }
    /// <summary>Fedora/RHEL dnf 包名。</summary>
    public string? DnfPackage { get; init; }
    /// <summary>Arch pacman 包名。</summary>
    public string? PacmanPackage { get; init; }
}

/// <summary>
/// 各端检测运行/调试环境；按平台尝试自动修复；失败时提供完整手动修复步骤。
/// </summary>
public static class ProgramEnvironmentChecker
{
    public static ProgramEnvCheckResult Check(ProgramLanguage lang)
    {
        // Android 无桌面运行时 PATH；先给出平台结论，再尽量探测（如 Termux 注入 PATH 的场景）
        if (OperatingSystem.IsAndroid() && lang is not ProgramLanguage.Batch)
        {
            var probe = CheckCore(lang);
            if (probe.Ok) return probe;
            return new ProgramEnvCheckResult
            {
                Ok = false,
                ToolName = probe.ToolName,
                Detail = Loc.T("Prog.Manual.AndroidHint", probe.ToolName),
                InstallUrl = probe.InstallUrl,
                WingetId = probe.WingetId,
                BrewFormula = probe.BrewFormula,
                AptPackage = probe.AptPackage,
                DnfPackage = probe.DnfPackage,
                PacmanPackage = probe.PacmanPackage
            };
        }

        return CheckCore(lang);
    }

    private static ProgramEnvCheckResult CheckCore(ProgramLanguage lang) => lang switch
    {
        ProgramLanguage.Python => CheckTool(
            display: "Python",
            commands: ["python", "python3", "py"],
            versionArgs: "--version",
            installUrl: "https://www.python.org/downloads/",
            wingetId: "Python.Python.3.12",
            brew: "python",
            apt: "python3",
            dnf: "python3",
            pacman: "python"),
        ProgramLanguage.CSharp => CheckTool(
            display: ".NET SDK",
            commands: ["dotnet"],
            versionArgs: "--list-sdks",
            installUrl: "https://dotnet.microsoft.com/download",
            wingetId: "Microsoft.DotNet.SDK.8",
            brew: "dotnet",
            apt: "dotnet-sdk-8.0",
            dnf: "dotnet-sdk-8.0",
            pacman: "dotnet-sdk"),
        ProgramLanguage.JavaScript => CheckTool(
            display: "Node.js",
            commands: ["node", "nodejs"],
            versionArgs: "--version",
            installUrl: "https://nodejs.org/",
            wingetId: "OpenJS.NodeJS.LTS",
            brew: "node",
            apt: "nodejs",
            dnf: "nodejs",
            pacman: "nodejs"),
        ProgramLanguage.PowerShell => CheckPowerShell(),
        ProgramLanguage.Bash => CheckBash(),
        ProgramLanguage.Batch => CheckBatch(),
        _ => new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = lang.ToString(),
            Detail = Loc.T("Prog.DetectUnsupported")
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

    /// <summary>
    /// 按当前 OS 尝试自动安装；失败时 Message 末尾附带完整手动修复方案。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TryAutoInstallAsync(
        ProgramLanguage lang, ProgramEnvCheckResult result, CancellationToken ct = default)
    {
        if (result.Ok)
            return (true, Loc.T("Prog.DetectAlreadyOk"));

        if (OperatingSystem.IsAndroid())
            return FailWithGuide(lang, result, Loc.T("Prog.Auto.AndroidNo"));

        if (OperatingSystem.IsWindows())
        {
            var win = await TryWindowsAutoInstallAsync(lang, result, ct).ConfigureAwait(false);
            if (win.Ok) return win;
            return FailWithGuide(lang, result, win.Message);
        }

        if (OperatingSystem.IsMacOS())
        {
            var mac = await TryMacAutoInstallAsync(lang, result, ct).ConfigureAwait(false);
            if (mac.Ok) return mac;
            return FailWithGuide(lang, result, mac.Message);
        }

        if (OperatingSystem.IsLinux())
        {
            var linux = await TryLinuxAutoInstallAsync(lang, result, ct).ConfigureAwait(false);
            if (linux.Ok) return linux;
            return FailWithGuide(lang, result, linux.Message);
        }

        return FailWithGuide(lang, result, Loc.T("Prog.Auto.UnsupportedOs"));
    }

    /// <summary>完整手动修复方案（各端步骤 + 下载地址）。</summary>
    public static string BuildManualRepairGuide(ProgramLanguage lang, ProgramEnvCheckResult result)
    {
        var tool = string.IsNullOrWhiteSpace(result.ToolName) ? lang.ToString() : result.ToolName;
        var url = result.InstallUrl ?? "";
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("Prog.Manual.Title", tool));
        sb.AppendLine();

        if (OperatingSystem.IsAndroid())
        {
            sb.AppendLine(Loc.T("Prog.Manual.AndroidBody", tool, url));
            return sb.ToString().TrimEnd();
        }

        if (OperatingSystem.IsWindows())
        {
            sb.AppendLine(Loc.T("Prog.Manual.WindowsBody",
                tool,
                result.WingetId ?? "(无)",
                url));
            AppendCommonVerify(sb, tool);
            return sb.ToString().TrimEnd();
        }

        if (OperatingSystem.IsMacOS())
        {
            sb.AppendLine(Loc.T("Prog.Manual.MacBody",
                tool,
                result.BrewFormula ?? "(无)",
                url));
            AppendCommonVerify(sb, tool);
            return sb.ToString().TrimEnd();
        }

        if (OperatingSystem.IsLinux())
        {
            sb.AppendLine(Loc.T("Prog.Manual.LinuxBody",
                tool,
                result.AptPackage ?? "(无)",
                result.DnfPackage ?? "(无)",
                result.PacmanPackage ?? "(无)",
                url));
            if (lang == ProgramLanguage.CSharp)
                sb.AppendLine().AppendLine(Loc.T("Prog.Manual.LinuxDotnetExtra"));
            AppendCommonVerify(sb, tool);
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine(Loc.T("Prog.Manual.GenericBody", tool, url));
        AppendCommonVerify(sb, tool);
        return sb.ToString().TrimEnd();
    }

    private static void AppendCommonVerify(StringBuilder sb, string tool)
    {
        sb.AppendLine();
        sb.AppendLine(Loc.T("Prog.Manual.Verify", tool));
    }

    private static (bool Ok, string Message) FailWithGuide(
        ProgramLanguage lang, ProgramEnvCheckResult result, string reason)
    {
        var msg = reason.TrimEnd()
                  + "\n\n────────────────\n\n"
                  + BuildManualRepairGuide(lang, result);
        return (false, msg);
    }

    private static async Task<(bool Ok, string Message)> TryWindowsAutoInstallAsync(
        ProgramLanguage lang, ProgramEnvCheckResult result, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(result.WingetId) && ExistsOnPath("winget"))
        {
            var (ok, output) = await RunProcessAsync(
                "winget",
                $"install -e --id {result.WingetId} --accept-package-agreements --accept-source-agreements",
                180_000, ct).ConfigureAwait(false);
            if (ok)
            {
                var again = Check(lang);
                if (again.Ok)
                    return (true, Loc.T("Prog.Auto.InstalledOk", result.ToolName));
                return (true, Loc.T("Prog.Auto.InstalledRestart", result.ToolName, Trim(output, 400)));
            }

            // winget 失败时尝试 chocolatey
            if (ExistsOnPath("choco") && !string.IsNullOrWhiteSpace(WindowsChocoPackage(lang)))
            {
                var pkg = WindowsChocoPackage(lang)!;
                var (cok, cout) = await RunProcessAsync(
                    "choco", $"install {pkg} -y", 180_000, ct).ConfigureAwait(false);
                if (cok)
                {
                    var again = Check(lang);
                    if (again.Ok)
                        return (true, Loc.T("Prog.Auto.InstalledOk", result.ToolName));
                    return (true, Loc.T("Prog.Auto.InstalledRestart", result.ToolName, Trim(cout, 400)));
                }
                return (false, Loc.T("Prog.Auto.WingetChocoFail", Trim(output, 300), Trim(cout, 300)));
            }

            return (false, Loc.T("Prog.Auto.WingetFail", Trim(output, 500)));
        }

        if (ExistsOnPath("choco") && !string.IsNullOrWhiteSpace(WindowsChocoPackage(lang)))
        {
            var pkg = WindowsChocoPackage(lang)!;
            var (ok, output) = await RunProcessAsync(
                "choco", $"install {pkg} -y", 180_000, ct).ConfigureAwait(false);
            if (ok)
            {
                var again = Check(lang);
                if (again.Ok)
                    return (true, Loc.T("Prog.Auto.InstalledOk", result.ToolName));
                return (true, Loc.T("Prog.Auto.InstalledRestart", result.ToolName, Trim(output, 400)));
            }
            return (false, Loc.T("Prog.Auto.ChocoFail", Trim(output, 500)));
        }

        return (false, Loc.T("Prog.Auto.WindowsNoPkgMgr"));
    }

    private static string? WindowsChocoPackage(ProgramLanguage lang) => lang switch
    {
        ProgramLanguage.Python => "python",
        ProgramLanguage.CSharp => "dotnet-8.0-sdk",
        ProgramLanguage.JavaScript => "nodejs-lts",
        ProgramLanguage.PowerShell => "powershell-core",
        ProgramLanguage.Bash => "git",
        _ => null
    };

    private static async Task<(bool Ok, string Message)> TryMacAutoInstallAsync(
        ProgramLanguage lang, ProgramEnvCheckResult result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(result.BrewFormula))
            return (false, Loc.T("Prog.Auto.MacNoFormula"));

        if (!ExistsOnPath("brew"))
            return (false, Loc.T("Prog.Auto.MacNoBrew"));

        var (ok, output) = await RunProcessAsync(
            "brew", $"install {result.BrewFormula}", 300_000, ct).ConfigureAwait(false);
        if (ok)
        {
            var again = Check(lang);
            if (again.Ok)
                return (true, Loc.T("Prog.Auto.InstalledOk", result.ToolName));
            return (true, Loc.T("Prog.Auto.InstalledRestart", result.ToolName, Trim(output, 400)));
        }
        return (false, Loc.T("Prog.Auto.BrewFail", Trim(output, 500)));
    }

    private static async Task<(bool Ok, string Message)> TryLinuxAutoInstallAsync(
        ProgramLanguage lang, ProgramEnvCheckResult result, CancellationToken ct)
    {
        // 优先非交互安装；无 root 时会失败并落到手动方案
        if (ExistsOnPath("apt-get") && !string.IsNullOrWhiteSpace(result.AptPackage))
        {
            var (ok, output) = await RunShellAsync(
                $"sudo -n apt-get update -qq && sudo -n apt-get install -y {result.AptPackage}",
                300_000, ct).ConfigureAwait(false);
            if (!ok)
            {
                // 无密码免密时再试无 sudo（用户已是 root）
                (ok, output) = await RunShellAsync(
                    $"apt-get update -qq && apt-get install -y {result.AptPackage}",
                    300_000, ct).ConfigureAwait(false);
            }
            if (ok)
            {
                var again = Check(lang);
                if (again.Ok)
                    return (true, Loc.T("Prog.Auto.InstalledOk", result.ToolName));
                return (true, Loc.T("Prog.Auto.InstalledRestart", result.ToolName, Trim(output, 400)));
            }
            return (false, Loc.T("Prog.Auto.AptFail", Trim(output, 500)));
        }

        if (ExistsOnPath("dnf") && !string.IsNullOrWhiteSpace(result.DnfPackage))
        {
            var (ok, output) = await RunShellAsync(
                $"sudo -n dnf install -y {result.DnfPackage}",
                300_000, ct).ConfigureAwait(false);
            if (!ok)
                (ok, output) = await RunShellAsync(
                    $"dnf install -y {result.DnfPackage}", 300_000, ct).ConfigureAwait(false);
            if (ok)
            {
                var again = Check(lang);
                if (again.Ok)
                    return (true, Loc.T("Prog.Auto.InstalledOk", result.ToolName));
                return (true, Loc.T("Prog.Auto.InstalledRestart", result.ToolName, Trim(output, 400)));
            }
            return (false, Loc.T("Prog.Auto.DnfFail", Trim(output, 500)));
        }

        if (ExistsOnPath("pacman") && !string.IsNullOrWhiteSpace(result.PacmanPackage))
        {
            var (ok, output) = await RunShellAsync(
                $"sudo -n pacman -Sy --noconfirm {result.PacmanPackage}",
                300_000, ct).ConfigureAwait(false);
            if (!ok)
                (ok, output) = await RunShellAsync(
                    $"pacman -Sy --noconfirm {result.PacmanPackage}", 300_000, ct).ConfigureAwait(false);
            if (ok)
            {
                var again = Check(lang);
                if (again.Ok)
                    return (true, Loc.T("Prog.Auto.InstalledOk", result.ToolName));
                return (true, Loc.T("Prog.Auto.InstalledRestart", result.ToolName, Trim(output, 400)));
            }
            return (false, Loc.T("Prog.Auto.PacmanFail", Trim(output, 500)));
        }

        return (false, Loc.T("Prog.Auto.LinuxNoPkgMgr"));
    }

    private static ProgramEnvCheckResult CheckTool(
        string display, string[] commands, string versionArgs,
        string installUrl, string? wingetId, string? brew,
        string? apt, string? dnf, string? pacman)
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
                        ? Loc.T("Prog.DetectFoundCmd", cmd)
                        : Loc.T("Prog.DetectFoundVer", cmd, ver),
                    InstallUrl = installUrl,
                    WingetId = wingetId,
                    BrewFormula = brew,
                    AptPackage = apt,
                    DnfPackage = dnf,
                    PacmanPackage = pacman
                };
            }
        }

        return new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = display,
            Detail = Loc.T("Prog.DetectMissing", display, string.Join(", ", commands)),
            InstallUrl = installUrl,
            WingetId = wingetId,
            BrewFormula = brew,
            AptPackage = apt,
            DnfPackage = dnf,
            PacmanPackage = pacman
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
                    ? Loc.T("Prog.DetectFoundPwsh")
                    : Loc.T("Prog.DetectFoundVer", "pwsh", Trim(output, 80)),
                InstallUrl = "https://aka.ms/powershell",
                WingetId = "Microsoft.PowerShell",
                BrewFormula = "powershell",
                AptPackage = "powershell",
                DnfPackage = "powershell",
                PacmanPackage = "powershell"
            };
        }

        if (OperatingSystem.IsWindows() && ExistsOnPath("powershell"))
        {
            return new ProgramEnvCheckResult
            {
                Ok = true,
                ToolName = "PowerShell",
                Detail = Loc.T("Prog.DetectFoundWinPs"),
                InstallUrl = "https://aka.ms/powershell",
                WingetId = "Microsoft.PowerShell"
            };
        }

        return new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = "PowerShell",
            Detail = Loc.T("Prog.DetectMissingPs"),
            InstallUrl = "https://aka.ms/powershell",
            WingetId = "Microsoft.PowerShell",
            BrewFormula = "powershell",
            AptPackage = "powershell",
            DnfPackage = "powershell",
            PacmanPackage = "powershell"
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
                    Detail = Loc.T("Prog.DetectFoundGitBash", gitBash),
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
                Detail = string.IsNullOrWhiteSpace(output)
                    ? Loc.T("Prog.DetectFoundCmd", "bash")
                    : Trim(output, 100),
                InstallUrl = "https://git-scm.com/downloads",
                WingetId = "Git.Git",
                BrewFormula = "bash",
                AptPackage = "bash",
                DnfPackage = "bash",
                PacmanPackage = "bash"
            };
        }

        return new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = "Bash",
            Detail = OperatingSystem.IsWindows()
                ? Loc.T("Prog.DetectMissingBashWin")
                : Loc.T("Prog.DetectMissingBash"),
            InstallUrl = "https://git-scm.com/downloads",
            WingetId = "Git.Git",
            BrewFormula = "bash",
            AptPackage = "bash",
            DnfPackage = "bash",
            PacmanPackage = "bash"
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
                Detail = Loc.T("Prog.DetectBatchOk"),
                InstallUrl = null
            };
        }

        return new ProgramEnvCheckResult
        {
            Ok = false,
            ToolName = "cmd",
            Detail = Loc.T("Prog.DetectBatchOnlyWin"),
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

    private static async Task<(bool Ok, string Output)> RunShellAsync(
        string command, int timeoutMs, CancellationToken ct)
    {
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var args = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"";
        return await RunProcessAsync(shell, args, timeoutMs, ct).ConfigureAwait(false);
    }

    private static string Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
