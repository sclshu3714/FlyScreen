using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using FlyScreen.Services;
using Microsoft.Win32;

namespace FlyScreen.LockInput;

/// <summary>安装 / 启停 Windows 锁屏输入助手服务（LocalSystem）。</summary>
public static class LockScreenInputServiceControl
{
    /// <summary>最近一次启用/停用失败原因（供状态栏展示）。</summary>
    public static string LastError { get; private set; } = "";

    private static string ServiceRegistryPath
        => @"SYSTEM\CurrentControlSet\Services\" + LockScreenInputProtocol.ServiceName;

    /// <summary>查注册表，避免 ServiceController 在服务不存在时抛一阶异常刷调试输出。</summary>
    public static bool IsServiceInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceRegistryPath);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsServiceRunning()
    {
        if (!IsServiceInstalled())
            return false;
        try
        {
            using var sc = new ServiceController(LockScreenInputProtocol.ServiceName);
            return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsProcessElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>与设置勾选对齐：开启则部署并启动服务；关闭则停止并删除服务。</summary>
    public static bool SetEnabled(bool enabled)
    {
        LastError = "";
        try
        {
            if (!enabled)
                return StopAndDelete();

            if (!IsProcessElevated())
            {
                LastError = "需要管理员权限才能安装锁屏输入服务";
                return false;
            }

            var exe = EnsureDeployedExe();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                LastError = "找不到助手组件（FlyScreen.LockInputHelper.bin）";
                return false;
            }

            if (IsServiceInstalled())
            {
                // 路径可能随版本更新，重建以保证 binPath 正确
                StopAndDelete();
            }

            if (!CreateService(exe))
                return false;

            if (!StartService())
            {
                if (string.IsNullOrWhiteSpace(LastError))
                    LastError = "服务已创建但启动失败";
                return false;
            }

            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public static string? EnsureDeployedExe()
    {
        try
        {
            var dir = Path.Combine(FlyScreenDataPaths.Root, "LockInputHelper");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, LockScreenInputProtocol.ExeName);

            using var src = OpenBundledStream();
            if (src == null) return File.Exists(dest) ? dest : null;

            var tmp = dest + ".tmp";
            using (var fs = File.Create(tmp))
                src.CopyTo(fs);

            try
            {
                if (File.Exists(dest))
                    File.Delete(dest);
            }
            catch
            {
                // 服务占用旧文件：仍返回已有路径
                try { File.Delete(tmp); } catch { /* ignore */ }
                return File.Exists(dest) ? dest : null;
            }

            File.Move(tmp, dest);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    private static Stream? OpenBundledStream()
    {
        foreach (var asm in new[] { Assembly.GetEntryAssembly(), typeof(LockScreenInputServiceControl).Assembly })
        {
            if (asm == null) continue;
            try
            {
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith(LockScreenInputProtocol.BinName, StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(LockScreenInputProtocol.ExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        var s = asm.GetManifestResourceStream(name);
                        if (s != null) return s;
                    }
                }
            }
            catch { /* next */ }
        }

        var dirs = new List<string>();
        void AddDir(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            try
            {
                d = Path.GetFullPath(d);
                if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                    dirs.Add(d);
            }
            catch { /* ignore */ }
        }

        AddDir(AppContext.BaseDirectory);
        AddDir(Path.GetDirectoryName(Environment.ProcessPath));
        try
        {
            AddDir(Path.GetDirectoryName(typeof(LockScreenInputServiceControl).Assembly.Location));
        }
        catch { /* ignore */ }

        foreach (var dir in dirs)
        {
            foreach (var name in new[] { LockScreenInputProtocol.BinName, LockScreenInputProtocol.ExeName })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                    return File.OpenRead(path);
            }
        }

        return null;
    }

    private static bool CreateService(string exePath)
    {
        // sc create … binPath= "C:\path\exe.exe --service"
        // 路径含空格时：binPath= "\"C:\Program Files\a.exe\" --service"
        var hasSpace = exePath.IndexOfAny([' ', '\t']) >= 0;
        var binInner = hasSpace ? $"\\\"{exePath}\\\" --service" : $"{exePath} --service";
        var args =
            $"create {LockScreenInputProtocol.ServiceName} " +
            $"binPath= \"{binInner}\" " +
            "start= auto obj= LocalSystem " +
            "DisplayName= \"玄鸟锁屏输入助手\"";

        var (ok, output) = RunSc(args);
        if (!ok)
        {
            LastError = SummarizeScError(output, "创建服务失败");
            return false;
        }

        RunSc($"description {LockScreenInputProtocol.ServiceName} \"允许对端在本机锁屏/登录界面注入键鼠\"");
        return true;
    }

    private static bool StartService()
    {
        if (!IsServiceInstalled())
        {
            LastError = "服务未安装";
            return false;
        }

        try
        {
            using var sc = new ServiceController(LockScreenInputProtocol.ServiceName);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                return true;
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            if (sc.Status == ServiceControllerStatus.Running)
                return true;
            LastError = $"服务状态异常：{sc.Status}";
            return false;
        }
        catch (Exception ex)
        {
            var (ok, output) = RunSc($"start {LockScreenInputProtocol.ServiceName}");
            if (ok && IsServiceRunning())
                return true;
            LastError = SummarizeScError(
                string.IsNullOrWhiteSpace(output) ? ex.Message : output,
                "启动服务失败");
            return false;
        }
    }

    private static bool StopAndDelete()
    {
        if (IsServiceInstalled())
        {
            try
            {
                using var sc = new ServiceController(LockScreenInputProtocol.ServiceName);
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                {
                    try
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }

            RunSc($"delete {LockScreenInputProtocol.ServiceName}");
            Thread.Sleep(400);
        }

        LockScreenInputClient.Reset();
        LastError = "";
        return !IsServiceInstalled() || !IsServiceRunning();
    }

    private static string SummarizeScError(string output, string fallback)
    {
        var text = (output ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("拒绝访问", StringComparison.Ordinal)
            || text.Contains("FAILED 5", StringComparison.OrdinalIgnoreCase))
            return "需要管理员权限才能安装锁屏输入服务";
        if (text.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || text.Contains("已存在", StringComparison.Ordinal))
            return "服务已存在但无法重建，请稍后重试";
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        if (text.Length > 160)
            text = text[..160] + "…";
        return text;
    }

    private static (bool Ok, string Output) RunSc(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return (false, "无法启动 sc.exe");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            return (p.ExitCode == 0, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
