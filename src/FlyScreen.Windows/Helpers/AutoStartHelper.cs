using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace FlyScreen.Helpers;

/// <summary>
/// 开机自启：使用「任务计划程序 + 最高权限」。
/// 本程序清单为 requireAdministrator，写入注册表 Run 会在登录时被 UAC 静默拦截，无法启动。
/// </summary>
public static class AutoStartHelper
{
    public const string AutostartArg = "--autostart";

    private const string TaskName = "FlyScreen Autostart";
    private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "FlyScreen";

    public static bool IsEnabled()
    {
        try
        {
            if (HasLegacyRunKey()) return true;
            if (!TaskExists()) return false;
            // 已禁用的任务不算开启（删除失败时会走 /DISABLE 兜底）
            return !IsTaskDisabled();
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            // 始终清掉无效的 Run 项，避免用户以为已自启
            RemoveLegacyRunKey();

            if (!enabled)
                return DisableAndDeleteTask();

            var exe = ResolveExecutablePath();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return false;

            // 任务已正确指向当前 exe 时跳过删建，避免启动/应用时反复 schtasks 卡住 UI/钩子
            if (IsEnabled() && TaskRunsExecutable(exe))
                return true;

            return CreateElevatedLogonTask(exe);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAutostartLaunch(IEnumerable<string>? args)
        => args != null && args.Any(a =>
            string.Equals(a, AutostartArg, StringComparison.OrdinalIgnoreCase));

    private static bool CreateElevatedLogonTask(string exePath)
    {
        var workDir = Path.GetDirectoryName(exePath) ?? "";
        var xml = BuildTaskXml(exePath, workDir);
        var xmlPath = Path.Combine(Path.GetTempPath(), $"FlyScreen-autostart-{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks /Create /XML 需要 Unicode (UTF-16 LE)
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);

            // 先删旧任务再创建，保证路径更新（发布新目录后重开自启）
            DisableAndDeleteTask();

            var (ok, _) = RunSchTasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            return ok && IsEnabled();
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* ignore */ }
        }
    }

    private static string BuildTaskXml(string exePath, string workDir)
    {
        static string Esc(string s) =>
            s.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal)
             .Replace("\"", "&quot;", StringComparison.Ordinal);

        // 登录后延迟数秒，等网卡/桌面就绪，便于自动连接对端
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>玄鸟开机自动启动并连接</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT15S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Esc(exePath)}</Command>
                  <Arguments>{AutostartArg}</Arguments>
                  <WorkingDirectory>{Esc(workDir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool TaskExists()
    {
        var (ok, _) = RunSchTasks($"/Query /TN \"{TaskName}\"");
        return ok;
    }

    /// <summary>关闭自启：先禁用再删除，降低「删不掉但仍会跑」的概率。</summary>
    private static bool DisableAndDeleteTask()
    {
        if (!TaskExists())
            return true;

        // 禁用：即便删除因权限失败，登录后也不会再拉起
        RunSchTasks($"/Change /TN \"{TaskName}\" /DISABLE");

        var (ok, output) = RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
        if (ok || IsTaskMissingOutput(output))
            return !TaskExists() || IsTaskDisabled();

        // 删除失败但已禁用 → 仍视为关闭成功
        if (IsTaskDisabled())
            return true;

        return !TaskExists();
    }

    private static bool IsTaskDisabled()
    {
        if (!TryReadTaskListVerbose(out var output))
            return false;

        // 只看「计划任务状态」行；勿匹配其它字段里的 Disabled（如 Delete Task If Not Rescheduled）
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            var isStateLine =
                t.StartsWith("Scheduled Task State", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("计划任务状态", StringComparison.Ordinal)
                || t.StartsWith("工作狀態", StringComparison.Ordinal) // 部分繁中
                || t.StartsWith("工作状态", StringComparison.Ordinal);
            if (!isStateLine) continue;

            var idx = t.IndexOf(':');
            var value = idx >= 0 ? t[(idx + 1)..].Trim() : t;
            return value.Contains("Disabled", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("已禁用", StringComparison.Ordinal)
                   || value.Equals("停用", StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>计划任务「要运行的任务」是否已是当前 exe（含 --autostart）。</summary>
    private static bool TaskRunsExecutable(string exePath)
    {
        if (!TryReadTaskListVerbose(out var output))
            return false;

        var full = Path.GetFullPath(exePath).Trim().Trim('"');
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            var isRunLine =
                t.StartsWith("Task To Run", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("要运行的任务", StringComparison.Ordinal)
                || t.StartsWith("要執行的工作", StringComparison.Ordinal);
            if (!isRunLine) continue;

            var idx = t.IndexOf(':');
            var value = idx >= 0 ? t[(idx + 1)..].Trim() : t;
            if (value.Contains(full, StringComparison.OrdinalIgnoreCase))
                return true;

            // 兼容短路径 / 大小写
            try
            {
                var cmd = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim('"');
                if (File.Exists(cmd)
                    && string.Equals(Path.GetFullPath(cmd), full, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    private static bool TryReadTaskListVerbose(out string output)
    {
        var (ok, text) = RunSchTasks($"/Query /TN \"{TaskName}\" /FO LIST /V");
        output = text;
        return ok && !string.IsNullOrWhiteSpace(text);
    }

    private static bool IsTaskMissingOutput(string output)
        => output.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
           || output.Contains("找不到", StringComparison.Ordinal)
           || output.Contains("ERROR: The system cannot find", StringComparison.OrdinalIgnoreCase)
           || output.Contains("系统找不到指定的文件", StringComparison.Ordinal);

    private static bool HasLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: false);
            return !string.IsNullOrWhiteSpace(key?.GetValue(LegacyValueName) as string);
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // ignore
        }
    }

    private static (bool ok, string output) RunSchTasks(string arguments)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                }
            };
            p.Start();
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "timeout");
            }

            var output = (stdout + Environment.NewLine + stderr).Trim();
            return (p.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return Path.GetFullPath(path);

        try
        {
            var mod = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mod) && File.Exists(mod))
                return Path.GetFullPath(mod);
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
