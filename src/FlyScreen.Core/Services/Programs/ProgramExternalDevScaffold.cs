using System.Text;

namespace FlyScreen.Services.Programs;

/// <summary>
/// 为程序目录生成 / 补齐「外部 VS / VS Code」开发文件。
/// 玄鸟进程内运行仍用 Roslyn；这些文件不进入 ExtraFiles。
/// C# DevHost 引用宿主/安装目录下的 <c>FlyScreen.Core.dll</c>（不引用源码 csproj）。
/// </summary>
public static class ProgramExternalDevScaffold
{
    public const string DevHostFileName = "DevHost.cs";
    public const string CsprojFileName = "program.csproj";
    public const string PropsFileName = "Directory.Build.props";
    public const string ReadmeFileName = "README-DEV.md";

    /// <summary>创建、同步依赖、接收共享后调用。</summary>
    public static void Ensure(
        string programFolder,
        ProgramLanguage language,
        IReadOnlyList<NugetPackageRef>? packages = null)
    {
        if (string.IsNullOrWhiteSpace(programFolder)) return;
        Directory.CreateDirectory(programFolder);

        WriteReadme(programFolder, language);
        WriteVsCodeExtensions(programFolder, language);

        if (language != ProgramLanguage.CSharp) return;

        Directory.CreateDirectory(Path.Combine(programFolder, "lib"));
        Directory.CreateDirectory(Path.Combine(programFolder, "refs"));

        File.WriteAllText(
            Path.Combine(programFolder, DevHostFileName),
            ProgramNugetResolver.BuildDevHostSource(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        File.WriteAllText(
            Path.Combine(programFolder, CsprojFileName),
            ProgramNugetResolver.BuildCsproj(packages),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        EnsureDirectoryBuildProps(programFolder);
    }

    /// <summary>
    /// 解析宿主目录（含 <c>FlyScreen.Core.dll</c>）：
    /// 安装目录或调试时主程序 exe 所在目录。
    /// </summary>
    public static string? TryFindHostDirectory()
    {
        var env = Environment.GetEnvironmentVariable("FLYSCREEN_HOST_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)
            && File.Exists(Path.Combine(env, "FlyScreen.Core.dll")))
            return Path.GetFullPath(env);

        var envDll = Environment.GetEnvironmentVariable("FLYSCREEN_CORE_DLL");
        if (!string.IsNullOrWhiteSpace(envDll) && File.Exists(envDll))
            return Path.GetFullPath(Path.GetDirectoryName(envDll)!);

        foreach (var start in CandidateSearchRoots())
        {
            if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start)) continue;
            var dll = Path.Combine(start, "FlyScreen.Core.dll");
            if (File.Exists(dll))
                return Path.GetFullPath(start);
        }

        return null;
    }

    /// <summary>解析 <c>FlyScreen.Core.dll</c> 完整路径。</summary>
    public static string? TryFindCoreDllPath()
    {
        var envDll = Environment.GetEnvironmentVariable("FLYSCREEN_CORE_DLL");
        if (!string.IsNullOrWhiteSpace(envDll) && File.Exists(envDll))
            return Path.GetFullPath(envDll);

        var host = TryFindHostDirectory();
        if (host == null) return null;
        var dll = Path.Combine(host, "FlyScreen.Core.dll");
        return File.Exists(dll) ? dll : null;
    }

    private static IEnumerable<string> CandidateSearchRoots()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p);
                if (!list.Contains(full, StringComparer.OrdinalIgnoreCase))
                    list.Add(full);
            }
            catch { /* ignore */ }
        }

        Add(AppContext.BaseDirectory);
        try
        {
            var loc = typeof(Fly).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(loc))
                Add(Path.GetDirectoryName(loc));
        }
        catch { /* ignore */ }

        Add(Directory.GetCurrentDirectory());

        // 常见安装位置（Windows）
        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(pf))
            {
                Add(Path.Combine(pf, "FlyScreen"));
                Add(Path.Combine(pf, "玄鸟"));
            }
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
                Add(Path.Combine(local, "FlyScreen"));
        }
        catch { /* ignore */ }

        return list;
    }

    private static void EnsureDirectoryBuildProps(string folder)
    {
        var path = Path.Combine(folder, PropsFileName);
        var host = TryFindHostDirectory();
        var coreDll = TryFindCoreDllPath();
        var legacyProjectRef = false;
        if (File.Exists(path))
        {
            try
            {
                var existing = File.ReadAllText(path);
                legacyProjectRef = existing.Contains("FlyScreenCoreProject", StringComparison.OrdinalIgnoreCase)
                    || existing.Contains("ProjectReference", StringComparison.OrdinalIgnoreCase)
                    || existing.Contains("FlyScreen.Core.csproj", StringComparison.OrdinalIgnoreCase);
            }
            catch { /* ignore */ }
        }

        // 已有 props、无宿主、且非旧版 ProjectReference 时不覆盖用户手改
        if (File.Exists(path) && host == null && coreDll == null && !legacyProjectRef)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("<Project>");
        sb.AppendLine("  <!-- 外部 VS / VS Code：引用安装目录或宿主 exe 旁的 FlyScreen.Core.dll（勿引用源码 csproj） -->");
        sb.AppendLine("  <PropertyGroup>");
        if (!string.IsNullOrWhiteSpace(host))
        {
            sb.Append("    <FlyScreenHostDir Condition=\"'$(FlyScreenHostDir)' == ''\">")
                .Append(EscapeXml(host))
                .AppendLine("</FlyScreenHostDir>");
        }
        else
        {
            sb.AppendLine("    <!-- 未自动找到宿主目录：设置 FLYSCREEN_HOST_DIR 或填写 FlyScreenHostDir -->");
            sb.AppendLine("    <FlyScreenHostDir Condition=\"'$(FlyScreenHostDir)' == '' and '$(FLYSCREEN_HOST_DIR)' != ''\">$(FLYSCREEN_HOST_DIR)</FlyScreenHostDir>");
        }

        if (!string.IsNullOrWhiteSpace(coreDll))
        {
            sb.Append("    <FlyScreenCoreDll Condition=\"'$(FlyScreenCoreDll)' == ''\">")
                .Append(EscapeXml(coreDll))
                .AppendLine("</FlyScreenCoreDll>");
        }
        else
        {
            sb.AppendLine("    <FlyScreenCoreDll Condition=\"'$(FlyScreenCoreDll)' == '' and '$(FlyScreenHostDir)' != ''\">$(FlyScreenHostDir)\\FlyScreen.Core.dll</FlyScreenCoreDll>");
            sb.AppendLine("    <FlyScreenCoreDll Condition=\"'$(FlyScreenCoreDll)' == '' and '$(FLYSCREEN_CORE_DLL)' != ''\">$(FLYSCREEN_CORE_DLL)</FlyScreenCoreDll>");
        }

        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("</Project>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteReadme(string folder, ProgramLanguage language)
    {
        var path = Path.Combine(folder, ReadmeFileName);
        var lang = ProgramLanguageInfo.DisplayName(language);
        var ext = ProgramLanguageInfo.Extension(language);
        var sb = new StringBuilder();
        sb.AppendLine("# 外部开发（VS / VS Code）");
        sb.AppendLine();
        sb.AppendLine($"语言：**{lang}**（入口 `main{ext}`）。");
        sb.AppendLine();
        sb.AppendLine("用 **Visual Studio** 或 **VS Code** 打开本文件夹即可编辑。");
        sb.AppendLine("玄鸟内置编辑器适合小改；完整开发请用外部 IDE，再回玄鸟运行/调试。");
        sb.AppendLine();

        if (language == ProgramLanguage.CSharp)
        {
            sb.AppendLine("## C#（DevHost）");
            sb.AppendLine();
            sb.AppendLine("- `main.cs` / `lib/*.cs`：脚本正文（与玄鸟一致，含 `ct` / `Delay` / `Fly`）。");
            sb.AppendLine("- `DevHost.cs`：仅外部入口，**勿**加入 `program.json` → `ExtraFiles`。");
            sb.AppendLine("- `Directory.Build.props`：`FlyScreenHostDir` / `FlyScreenCoreDll` 指向**已安装或调试中的玄鸟目录**下的 `FlyScreen.Core.dll`（不是源码 `.csproj`）。");
            sb.AppendLine();
            sb.AppendLine("若 props 路径不对，可设置环境变量 `FLYSCREEN_HOST_DIR`（玄鸟 exe 所在目录），或：");
            sb.AppendLine();
            sb.AppendLine("```bat");
            sb.AppendLine("dotnet build -p:FlyScreenHostDir=C:\\Path\\To\\FlyScreen");
            sb.AppendLine("dotnet run --project program.csproj");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("`Ctrl+C` 取消 `ct`。设备类 `Fly.*` 请在手机玄鸟验证。");
        }
        else
        {
            sb.AppendLine("## 运行");
            sb.AppendLine();
            sb.AppendLine("在本目录用对应运行时执行入口文件，或直接在玄鸟中运行。");
            sb.AppendLine("工作目录应为程序文件夹（与玄鸟一致）。");
            if (language == ProgramLanguage.PowerShell)
                sb.AppendLine("PowerShell 在玄鸟内会注入 `Fly.*` 设置桥；外部请自行读 `settings.json` 或回玄鸟跑。");
        }

        sb.AppendLine();
        sb.AppendLine("## VS Code");
        sb.AppendLine();
        sb.AppendLine("```bat");
        sb.AppendLine("code .");
        sb.AppendLine("```");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteVsCodeExtensions(string folder, ProgramLanguage language)
    {
        var dir = Path.Combine(folder, ".vscode");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "extensions.json");
        if (File.Exists(path)) return;

        var ext = language switch
        {
            ProgramLanguage.CSharp => """["ms-dotnettools.csharp", "ms-dotnettools.csdevkit"]""",
            ProgramLanguage.Python => """["ms-python.python"]""",
            ProgramLanguage.JavaScript => """["dbaeumer.vscode-eslint"]""",
            ProgramLanguage.PowerShell => """["ms-vscode.powershell"]""",
            ProgramLanguage.Bash => """["mads-hartmann.bash-ide-vscode"]""",
            _ => """[]"""
        };
        File.WriteAllText(path,
            "{\n  \"recommendations\": " + ext + "\n}\n",
            new UTF8Encoding(false));
    }

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
