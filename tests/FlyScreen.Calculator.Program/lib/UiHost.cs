using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace FlyScreen.ProgramDevHost;

/// <summary>
/// 脚本侧 UI 宿主：在独立 STA 进程中加载 MainWindow.xaml。
/// 玄鸟为 Maui/WinUI，进程内加载 WPF 会原生崩溃（0xc000041d），故必须进程外开窗。
/// 使用反射启动 Process，避免脚本编译依赖 System.Diagnostics.Process 引用。
/// </summary>
public static class UiHost
{
    public static Task<bool?> ShowMainWindowAsync(CancellationToken token)
    {
        var xamlPath = FindProgramFile("MainWindow.xaml");
        return ShowOutOfProcessAsync(xamlPath, token);
    }

    static async Task<bool?> ShowOutOfProcessAsync(string xamlPath, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        var xamlTemp = Path.Combine(Path.GetTempPath(), "fly-wpf-" + id + ".xaml");
        var ps1 = Path.Combine(Path.GetTempPath(), "fly-wpf-" + id + ".ps1");

        var enginePath = FindProgramFile("CalculatorSession.cs");
        var cleaned = CleanLooseXaml(await File.ReadAllTextAsync(xamlPath, token).ConfigureAwait(false));
        await File.WriteAllTextAsync(xamlTemp, cleaned, Encoding.UTF8, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(ps1, BuildPowerShell(xamlTemp, enginePath), Encoding.UTF8, token).ConfigureAwait(false);

        try
        {
            var proc = StartPowerShell(ps1);
            try
            {
                await using var reg = token.Register(() => TryKill(proc));

                var stderrTask = ReadStdErrAsync(proc, token);
                await WaitForExitAsync(proc, token).ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                var exitCode = GetExitCode(proc);

                if (exitCode != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(stderr) ? $"exit={exitCode}" : stderr.Trim();
                    throw new InvalidOperationException("WPF 窗口进程失败: " + detail);
                }

                return true;
            }
            finally
            {
                TryDispose(proc);
            }
        }
        finally
        {
            TryDelete(ps1);
            TryDelete(xamlTemp);
        }
    }

    static object StartPowerShell(string ps1)
    {
        var asm = ResolveProcessAssembly();
        var psiType = asm.GetType("System.Diagnostics.ProcessStartInfo", throwOnError: true)!;
        var procType = asm.GetType("System.Diagnostics.Process", throwOnError: true)!;

        var psi = Activator.CreateInstance(psiType)!;
        psiType.GetProperty("FileName")!.SetValue(psi, "powershell.exe");
        psiType.GetProperty("UseShellExecute")!.SetValue(psi, false);
        psiType.GetProperty("CreateNoWindow")!.SetValue(psi, true);
        psiType.GetProperty("RedirectStandardError")!.SetValue(psi, true);
        psiType.GetProperty("RedirectStandardOutput")!.SetValue(psi, true);

        var argList = psiType.GetProperty("ArgumentList")!.GetValue(psi)!;
        var add = argList.GetType().GetMethod("Add", [typeof(string)])!;
        foreach (var a in new[] { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", ps1 })
            add.Invoke(argList, [a]);

        var start = procType.GetMethod("Start", [psiType])
            ?? throw new InvalidOperationException("找不到 Process.Start(ProcessStartInfo)。");
        var proc = start.Invoke(null, [psi])
            ?? throw new InvalidOperationException("无法启动 powershell 以显示 WPF 窗口。");
        return proc;
    }

    static Assembly ResolveProcessAssembly()
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(a.GetName().Name, "System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase))
                return a;
        }

        try
        {
            return Assembly.Load("System.Diagnostics.Process, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
        }
        catch
        {
            return Assembly.Load("System.Diagnostics.Process");
        }
    }

    static async Task WaitForExitAsync(object proc, CancellationToken token)
    {
        var mi = proc.GetType().GetMethod("WaitForExitAsync", [typeof(CancellationToken)]);
        if (mi != null)
        {
            var task = (Task)mi.Invoke(proc, [token])!;
            await task.ConfigureAwait(false);
            return;
        }

        // 兜底：轮询 HasExited
        var hasExited = proc.GetType().GetProperty("HasExited")!;
        while (!(bool)hasExited.GetValue(proc)!)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    static async Task<string> ReadStdErrAsync(object proc, CancellationToken token)
    {
        var prop = proc.GetType().GetProperty("StandardError")!;
        var reader = prop.GetValue(proc)!;
        var read = reader.GetType().GetMethod("ReadToEndAsync", [typeof(CancellationToken)])
            ?? reader.GetType().GetMethod("ReadToEndAsync", Type.EmptyTypes);
        if (read == null) return "";
        var args = read.GetParameters().Length == 1 ? new object[] { token } : Array.Empty<object>();
        var task = (Task<string>)read.Invoke(reader, args)!;
        return await task.ConfigureAwait(false);
    }

    static int GetExitCode(object proc)
        => (int)proc.GetType().GetProperty("ExitCode")!.GetValue(proc)!;

    static void TryKill(object proc)
    {
        try
        {
            var hasExited = (bool)proc.GetType().GetProperty("HasExited")!.GetValue(proc)!;
            if (hasExited) return;
            var kill = proc.GetType().GetMethod("Kill", [typeof(bool)])
                ?? proc.GetType().GetMethod("Kill", Type.EmptyTypes);
            if (kill == null) return;
            kill.Invoke(proc, kill.GetParameters().Length == 1 ? [true] : null);
        }
        catch
        {
            // ignore
        }
    }

    static void TryDispose(object proc)
    {
        try { (proc as IDisposable)?.Dispose(); } catch { /* ignore */ }
    }

    static string CleanLooseXaml(string text)
    {
        text = Regex.Replace(text, @"\s+x:Class=""[^""]*""", "");
        text = Regex.Replace(text, @"\s+xmlns:local=""[^""]*""", "");
        text = Regex.Replace(text, @"\s+d:\w+=(""[^""]*""|'[^']*')", "");
        return text;
    }

    static string BuildPowerShell(string cleanedXamlPath, string enginePath)
    {
        var pathLit = "'" + cleanedXamlPath.Replace("'", "''") + "'";
        var engineLit = "'" + enginePath.Replace("'", "''") + "'";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Add-Type -AssemblyName PresentationFramework");
        sb.AppendLine("Add-Type -AssemblyName PresentationCore");
        sb.AppendLine("Add-Type -AssemblyName WindowsBase");
        sb.AppendLine("Add-Type -AssemblyName System.Xaml");
        sb.AppendLine("Add-Type -Path " + engineLit);
        sb.AppendLine("$path = " + pathLit);
        sb.AppendLine("$reader = [System.Xml.XmlReader]::Create($path)");
        sb.AppendLine("try { $win = [System.Windows.Markup.XamlReader]::Load($reader) } finally { $reader.Close() }");
        sb.AppendLine("if ($win -eq $null) { throw 'XamlReader.Load returned null' }");
        sb.AppendLine("$script:calc = New-Object FlyScreen.ProgramDevHost.CalculatorSession");
        sb.AppendLine("$script:expr = $win.FindName('ExprText')");
        sb.AppendLine("$script:result = $win.FindName('ResultText')");
        sb.AppendLine("if ($script:expr -eq $null -or $script:result -eq $null) { throw 'Calculator display not found' }");
        sb.AppendLine("$script:update = {");
        sb.AppendLine("    $script:expr.Text = $script:calc.ExpressionText");
        sb.AppendLine("    $script:result.Text = $script:calc.ResultText");
        sb.AppendLine("}");
        sb.AppendLine("function Wire-Buttons($node) {");
        sb.AppendLine("    foreach ($child in [System.Windows.LogicalTreeHelper]::GetChildren($node)) {");
        sb.AppendLine("        if ($child -is [System.Windows.Controls.Button]) {");
        sb.AppendLine("            $null = $child.Add_Click({");
        sb.AppendLine("                param($sender, $e)");
        sb.AppendLine("                $tag = [string]$sender.Tag");
        sb.AppendLine("                if ([string]::IsNullOrEmpty($tag)) { return }");
        sb.AppendLine("                $script:calc.Input($tag)");
        sb.AppendLine("                & $script:update");
        sb.AppendLine("            })");
        sb.AppendLine("        }");
        sb.AppendLine("        if ($child -is [System.Windows.DependencyObject]) { Wire-Buttons $child }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("Wire-Buttons $win");
        sb.AppendLine("& $script:update");
        sb.AppendLine("$win.Add_PreviewKeyDown({");
        sb.AppendLine("    param($sender, $e)");
        sb.AppendLine("    $shift = ([System.Windows.Input.Keyboard]::Modifiers -band [System.Windows.Input.ModifierKeys]::Shift) -ne 0");
        sb.AppendLine("    $tag = [FlyScreen.ProgramDevHost.CalculatorSession]::TagFromKey($e.Key.ToString(), [bool]$shift)");
        sb.AppendLine("    if ($tag) {");
        sb.AppendLine("        $script:calc.Input($tag)");
        sb.AppendLine("        & $script:update");
        sb.AppendLine("        $e.Handled = $true");
        sb.AppendLine("    }");
        sb.AppendLine("})");
        sb.AppendLine("$win.Add_Loaded({ $null = $win.Focus() })");
        sb.AppendLine("$app = New-Object System.Windows.Application");
        sb.AppendLine("$app.ShutdownMode = [System.Windows.ShutdownMode]::OnMainWindowClose");
        sb.AppendLine("[void]$app.Run($win)");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    public static string FindProgramFile(string fileName)
    {
        if (!string.IsNullOrWhiteSpace(Fly.ProgramDirectory))
        {
            var bound = Path.Combine(Fly.ProgramDirectory, fileName);
            if (File.Exists(bound))
                return bound;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent!)
            {
                var path = Path.Combine(dir.FullName, fileName);
                if (File.Exists(path))
                    return path;
            }
        }

        var programsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyScreen", "Programs");
        if (Directory.Exists(programsRoot))
        {
            string? hit = null;
            foreach (var dir in Directory.EnumerateDirectories(programsRoot))
            {
                var path = Path.Combine(dir, fileName);
                if (!File.Exists(path) || !File.Exists(Path.Combine(dir, "program.json")))
                    continue;
                if (hit != null)
                {
                    hit = null;
                    break;
                }
                hit = path;
            }
            if (hit != null)
                return hit;
        }

        throw new FileNotFoundException($"找不到 {fileName}（请放在程序目录下）。", fileName);
    }
}
