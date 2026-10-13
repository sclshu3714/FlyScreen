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
            if (TaskExists()) return true;

            // 兼容旧版：若仍留着 Run 项也算“已开启”，但实际往往启动失败
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: false);
            return !string.IsNullOrWhiteSpace(key?.GetValue(LegacyValueName) as string);
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
                return DeleteTask();

            var exe = ResolveExecutablePath();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return false;

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
            DeleteTask();

            var (ok, _) = RunSchTasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            return ok && TaskExists();
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

    private static bool DeleteTask()
    {
        // 不存在时也视为成功
        var (ok, output) = RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
        if (ok) return true;
        if (output.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
            || output.Contains("找不到", StringComparison.Ordinal)
            || output.Contains("ERROR: The system cannot find", StringComparison.OrdinalIgnoreCase))
            return true;
        // 查询不存在
        return !TaskExists();
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
