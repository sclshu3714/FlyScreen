using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FlyScreen.Tool.DeveloperTools.Scaffold;

/// <summary>按现有 Tools 结构生成独立插件工具工程。</summary>
public static class ToolScaffoldGenerator
{
    public static ToolScaffoldResult Generate(ToolScaffoldRequest request)
    {
        var err = ToolScaffoldNaming.Validate(request);
        if (err != null)
            return Fail(err);

        var id = ToolScaffoldNaming.BuildIdentity(request.ProjectNameEn);
        var outputRoot = Path.GetFullPath(request.OutputRoot.Trim());
        var projectFolder = Path.Combine(outputRoot, id.FolderName);
        if (Directory.Exists(projectFolder) && Directory.EnumerateFileSystemEntries(projectFolder).Any())
            return Fail($"目标目录非空：{projectFolder}");

        Directory.CreateDirectory(projectFolder);
        var written = new List<string>();
        var needWin = request.SupportDesktop;
        var needDesktop = request.SupportDesktop;
        var needMaui = request.SupportPhone || request.SupportDesktop; // 手机必 Maui；电脑也可带 Maui-Win
        // 电脑端：Win + Desktop；手机端：Maui。两端都选时三端齐全。
        // 仅手机：只有 Maui（无 Win/Desktop）。
        if (!request.SupportDesktop)
        {
            needWin = false;
            needDesktop = false;
            needMaui = true;
        }
        else if (!request.SupportPhone)
        {
            needWin = true;
            needDesktop = true;
            needMaui = true; // 与 ColorPicker 对齐，电脑工具仍带 Maui（Maui-Win）
        }
        else
        {
            needWin = true;
            needDesktop = true;
            needMaui = true;
        }

        // 后台模式可不生成 UI 工程
        if (request.UiMode == ToolUiMode.Background)
        {
            needWin = false;
            needDesktop = false;
            needMaui = false;
        }

        var repo = TryFindRepoRoot(outputRoot) ?? TryFindRepoRoot(AppContext.BaseDirectory)
                   ?? TryFindRepoRoot(Directory.GetCurrentDirectory());
        var refs = BuildProjectRefs(projectFolder, repo);

        var coreDir = Path.Combine(projectFolder, id.AssemblyName);
        Write(written, Path.Combine(coreDir, id.AssemblyName + ".csproj"), BuildCoreCsproj(id, refs));
        Write(written, Path.Combine(coreDir, "manifest.json"), BuildManifest(request, id));
        Write(written, Path.Combine(coreDir, "Module.cs"), BuildModule(request, id));
        Write(written, Path.Combine(coreDir, id.FolderName + "Localization.cs"), BuildLocalization(request, id));
        Write(written, Path.Combine(coreDir, id.FolderName + "Icons.cs"), BuildIcons(id));
        Write(written, Path.Combine(coreDir, id.FolderName + "Service.cs"), BuildService(request, id));
        Directory.CreateDirectory(Path.Combine(coreDir, "Icons"));
        WritePlaceholderPng(Path.Combine(coreDir, "Icons", id.IconKey + ".png"));
        written.Add(Path.Combine(coreDir, "Icons", id.IconKey + ".png"));

        if (needWin)
        {
            var win = Path.Combine(projectFolder, id.AssemblyName + ".Ui.Win");
            Write(written, Path.Combine(win, id.AssemblyName + ".Ui.Win.csproj"), BuildUiWinCsproj(id, refs));
            Write(written, Path.Combine(win, id.FolderName + "WinUiContributor.cs"), BuildWinContributor(request, id));
            if (request.UiMode == ToolUiMode.Workspace)
            {
                Write(written, Path.Combine(win, id.FolderName + "Panel.xaml"), BuildWinPanelXaml(id));
                Write(written, Path.Combine(win, id.FolderName + "Panel.xaml.cs"), BuildWinPanelCode(id));
            }
            else if (request.UiMode == ToolUiMode.Dialog)
            {
                Write(written, Path.Combine(win, id.FolderName + "Dialog.xaml"), BuildWinDialogXaml(id));
                Write(written, Path.Combine(win, id.FolderName + "Dialog.xaml.cs"), BuildWinDialogCode(id));
            }
        }

        if (needMaui)
        {
            var maui = Path.Combine(projectFolder, id.AssemblyName + ".Ui.Maui");
            Write(written, Path.Combine(maui, id.AssemblyName + ".Ui.Maui.csproj"), BuildUiMauiCsproj(id, refs));
            Write(written, Path.Combine(maui, "ToolMauiTheme.cs"), BuildMauiTheme(id));
            Write(written, Path.Combine(maui, id.FolderName + "MauiUiContributor.cs"), BuildMauiContributor(request, id));
            if (request.UiMode == ToolUiMode.Workspace)
                Write(written, Path.Combine(maui, id.FolderName + "Panel.cs"), BuildMauiPanel(id));
            else if (request.UiMode == ToolUiMode.Dialog)
                Write(written, Path.Combine(maui, id.FolderName + "DialogPage.cs"), BuildMauiDialog(id));
        }

        if (needDesktop)
        {
            var desk = Path.Combine(projectFolder, id.AssemblyName + ".Ui.Desktop");
            Write(written, Path.Combine(desk, id.AssemblyName + ".Ui.Desktop.csproj"), BuildUiDesktopCsproj(id, refs));
            Write(written, Path.Combine(desk, id.FolderName + "DesktopUiContributor.cs"), BuildDesktopContributor(request, id));
            if (request.UiMode == ToolUiMode.Workspace)
            {
                Write(written, Path.Combine(desk, id.FolderName + "Panel.axaml"), BuildDesktopPanelAxaml(id));
                Write(written, Path.Combine(desk, id.FolderName + "Panel.axaml.cs"), BuildDesktopPanelCode(id));
            }
            else if (request.UiMode == ToolUiMode.Dialog)
            {
                Write(written, Path.Combine(desk, id.FolderName + "DialogWindow.axaml"), BuildDesktopDialogAxaml(id));
                Write(written, Path.Combine(desk, id.FolderName + "DialogWindow.axaml.cs"), BuildDesktopDialogCode(id));
            }
        }

        Write(written, Path.Combine(projectFolder, "README.md"),
            ToolScaffoldDocs.BuildReadme(request, id, needWin, needMaui, needDesktop));
        // 兼容旧入口
        Write(written, Path.Combine(projectFolder, "README-DEV.md"),
            "本说明已合并到 **README.md**。请打开 README.md 与 `docs/` 目录。\n");
        Write(written, Path.Combine(projectFolder, "docs", "manifest.md"),
            ToolScaffoldDocs.BuildManifestDoc(id));
        Write(written, Path.Combine(projectFolder, "docs", "ui-modes.md"),
            ToolScaffoldDocs.BuildUiModesDoc(request, id));
        Write(written, Path.Combine(projectFolder, ".vscode", "extensions.json"),
            "{\n  \"recommendations\": [\"ms-dotnettools.csharp\", \"ms-dotnettools.csdevkit\"]\n}\n");

        var projectPaths = new List<string>
        {
            Path.Combine(projectFolder, id.AssemblyName, id.AssemblyName + ".csproj")
        };
        if (needWin)
            projectPaths.Add(Path.Combine(projectFolder, id.AssemblyName + ".Ui.Win", id.AssemblyName + ".Ui.Win.csproj"));
        if (needDesktop)
            projectPaths.Add(Path.Combine(projectFolder, id.AssemblyName + ".Ui.Desktop", id.AssemblyName + ".Ui.Desktop.csproj"));
        if (needMaui)
            projectPaths.Add(Path.Combine(projectFolder, id.AssemblyName + ".Ui.Maui", id.AssemblyName + ".Ui.Maui.csproj"));

        var slnPath = Path.Combine(projectFolder, id.AssemblyName + ".sln");
        if (!TryWriteSolution(slnPath, projectFolder, projectPaths, out var slnError))
        {
            // 回退手写 sln
            Write(written, slnPath, BuildSolutionFile(id, projectPaths, projectFolder));
        }
        else
        {
            written.Add(slnPath);
        }

        var notes = new StringBuilder();
        notes.AppendLine($"已生成工具「{request.DisplayNameZh}」（{id.ToolId}）");
        notes.AppendLine(projectFolder);
        notes.AppendLine($"请打开解决方案：{slnPath}");
        if (!string.IsNullOrWhiteSpace(slnError))
            notes.AppendLine("（dotnet new sln 提示：" + slnError + "，已手写 .sln）");

        if (request.RegisterInRepo)
        {
            if (repo != null && IsUnderTools(projectFolder, repo))
            {
                TryAddToSolution(repo, projectFolder, id, needWin, needMaui, needDesktop, notes);
                TryPatchPackScript(repo, id, needWin, needMaui, needDesktop, notes);
                TryPatchToolIds(repo, id, notes);
                notes.AppendLine("请重新编译宿主并执行 Pack-FlyTools 后，在工具页安装验证。");
            }
            else
            {
                notes.AppendLine("输出不在仓库 src/Tools 下，已跳过写入 FlyScreen.sln / Pack-FlyTools；见 README.md。");
            }
        }

        return new ToolScaffoldResult
        {
            Ok = true,
            Message = notes.ToString().Trim(),
            ProjectFolder = projectFolder,
            ToolId = id.ToolId,
            WrittenFiles = written
        };
    }

    /// <summary>
    /// 默认生成位置：<c>%AppData%/FlyScreen/Code</c>（安装包与调试均可写）。
    /// 不再默认 <c>src/Tools</c>（正式安装没有源码树）。
    /// </summary>
    public static string TryFindDefaultOutputRoot()
    {
        try
        {
            var code = Path.Combine(FlyScreen.Services.FlyScreenDataPaths.Root, "Code");
            Directory.CreateDirectory(code);
            return code;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "FlyScreen", "Code");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static bool IsRepoToolsDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var repo = TryFindRepoRoot(full);
            if (repo == null) return false;
            var tools = Path.GetFullPath(Path.Combine(repo, "src", "Tools"));
            return string.Equals(full.TrimEnd('\\', '/'), tools.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(tools + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static ToolScaffoldResult Fail(string msg)
        => new() { Ok = false, Message = msg };

    private static bool TryWriteSolution(
        string slnPath, string projectFolder, IReadOnlyList<string> projectPaths, out string error)
    {
        error = "";
        try
        {
            var name = Path.GetFileNameWithoutExtension(slnPath);
            var psiNew = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"new sln -n \"{name}\" -o \"{projectFolder}\" --force",
                WorkingDirectory = projectFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psiNew))
            {
                var stderr = p?.StandardError.ReadToEnd() ?? "";
                p?.WaitForExit(60_000);
                if (p is not { ExitCode: 0 })
                {
                    error = stderr.Trim();
                    return false;
                }
            }

            foreach (var proj in projectPaths)
            {
                var psiAdd = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"sln \"{slnPath}\" add \"{proj}\"",
                    WorkingDirectory = projectFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psiAdd);
                var stderr = p?.StandardError.ReadToEnd() ?? "";
                p?.WaitForExit(60_000);
                if (p is not { ExitCode: 0 })
                {
                    error = stderr.Trim();
                    return false;
                }
            }

            return File.Exists(slnPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>手写经典 .sln（dotnet CLI 不可用时回退）。</summary>
    private static string BuildSolutionFile(
        ToolScaffoldIdentity id, IReadOnlyList<string> projectPaths, string projectFolder)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        sb.AppendLine("# Visual Studio Version 17");
        sb.AppendLine("VisualStudioVersion = 17.0.31903.272");
        sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");
        var guids = new List<(string Name, string Rel, string Guid)>();
        foreach (var proj in projectPaths)
        {
            var name = Path.GetFileNameWithoutExtension(proj);
            var rel = Path.GetRelativePath(projectFolder, proj).Replace('/', '\\');
            var g = Guid.NewGuid().ToString("B").ToUpperInvariant();
            guids.Add((name, rel, g));
            sb.AppendLine($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{rel}\", \"{g}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var g in guids)
        {
            sb.AppendLine($"\t\t{g.Guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"\t\t{g.Guid}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine($"\t\t{g.Guid}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine($"\t\t{g.Guid}.Release|Any CPU.Build.0 = Release|Any CPU");
        }
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        sb.AppendLine("\t\tHideSolutionNode = FALSE");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");
        return sb.ToString();
    }

    private static void Write(List<string> written, string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        written.Add(path);
    }

    private static string? TryFindRepoRoot(string? start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FlyScreen.sln"))
                    && Directory.Exists(Path.Combine(dir.FullName, "src", "Tools")))
                    return dir.FullName;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static bool IsUnderTools(string projectFolder, string repo)
    {
        var tools = Path.GetFullPath(Path.Combine(repo, "src", "Tools"));
        var full = Path.GetFullPath(projectFolder);
        return full.StartsWith(tools + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(tools + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryAddToSolution(
        string repo, string projectFolder, ToolScaffoldIdentity id,
        bool win, bool maui, bool desktop, StringBuilder notes)
    {
        var sln = Path.Combine(repo, "FlyScreen.sln");
        if (!File.Exists(sln)) return;
        var projects = new List<string>
        {
            Path.Combine(projectFolder, id.AssemblyName, id.AssemblyName + ".csproj")
        };
        if (win) projects.Add(Path.Combine(projectFolder, id.AssemblyName + ".Ui.Win", id.AssemblyName + ".Ui.Win.csproj"));
        if (maui) projects.Add(Path.Combine(projectFolder, id.AssemblyName + ".Ui.Maui", id.AssemblyName + ".Ui.Maui.csproj"));
        if (desktop) projects.Add(Path.Combine(projectFolder, id.AssemblyName + ".Ui.Desktop", id.AssemblyName + ".Ui.Desktop.csproj"));

        foreach (var proj in projects)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"sln \"{sln}\" add \"{proj}\"",
                    WorkingDirectory = repo,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(60_000);
                if (p is { ExitCode: 0 })
                    notes.AppendLine($"已加入解决方案：{Path.GetFileName(proj)}");
                else
                    notes.AppendLine($"加入解决方案失败：{Path.GetFileName(proj)}（可手动 dotnet sln add）");
            }
            catch (Exception ex)
            {
                notes.AppendLine($"加入解决方案异常：{ex.Message}");
            }
        }
    }

    private static void TryPatchPackScript(
        string repo, ToolScaffoldIdentity id, bool win, bool maui, bool desktop, StringBuilder notes)
    {
        var path = Path.Combine(repo, "build", "Pack-FlyTools.ps1");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        if (text.Contains($"Id = \"{id.ToolId}\"", StringComparison.OrdinalIgnoreCase))
        {
            notes.AppendLine("Pack-FlyTools.ps1 已含该工具，跳过。");
            return;
        }

        var ui = new List<string>();
        var baseRel = $"src/Tools/{id.FolderName}";
        if (win) ui.Add($"            \"{baseRel}/{id.AssemblyName}.Ui.Win/{id.AssemblyName}.Ui.Win.csproj\"");
        if (desktop) ui.Add($"            \"{baseRel}/{id.AssemblyName}.Ui.Desktop/{id.AssemblyName}.Ui.Desktop.csproj\"");
        if (maui) ui.Add($"            \"{baseRel}/{id.AssemblyName}.Ui.Maui/{id.AssemblyName}.Ui.Maui.csproj\"");
        var uiLines = ui.Count == 0
            ? "        UiProjs = @()"
            : "        UiProjs = @(\r\n" + string.Join(",\r\n", ui) + "\r\n        )";

        var entry =
            "    ,\r\n" +
            "    @{\r\n" +
            $"        Id = \"{id.ToolId}\"\r\n" +
            $"        Proj = \"{baseRel}/{id.AssemblyName}/{id.AssemblyName}.csproj\"\r\n" +
            $"        Manifest = \"{baseRel}/{id.AssemblyName}/manifest.json\"\r\n" +
            uiLines + "\r\n" +
            "    }\r\n";

        const string anchor = "function Test-AllowedFlyToolDest";
        var anchorIdx = text.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIdx < 0)
        {
            notes.AppendLine("未能自动修改 Pack-FlyTools.ps1，请按 README 手动添加。");
            return;
        }

        // 向前找到 $tools 数组的收尾 `)`
        var closeIdx = text.LastIndexOf(')', anchorIdx);
        if (closeIdx < 0)
        {
            notes.AppendLine("未能定位 Pack-FlyTools $tools 收尾，请手动添加。");
            return;
        }

        text = text.Insert(closeIdx, entry);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        notes.AppendLine("已写入 Pack-FlyTools.ps1。");
    }

    private static void TryPatchToolIds(string repo, ToolScaffoldIdentity id, StringBuilder notes)
    {
        var path = Path.Combine(repo, "src", "FlyScreen.Core", "Services", "Tools", "ToolIds.cs");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var constName = id.FolderName; // PascalCase
        if (text.Contains($" {constName} =", StringComparison.Ordinal)
            || text.Contains($"\"{id.ToolId}\"", StringComparison.Ordinal))
        {
            notes.AppendLine("ToolIds.cs 已含该 Id，跳过。");
            return;
        }

        var insert = $"    public const string {constName} = \"{id.ToolId}\";\r\n";
        var idx = text.LastIndexOf('}');
        if (idx < 0)
        {
            notes.AppendLine("ToolIds.cs 格式异常，请手动添加。");
            return;
        }
        // 插入到类最后一个 const 后：找倒数第二个 }
        var classClose = text.LastIndexOf('}', idx - 1);
        if (classClose < 0) classClose = idx;
        // 更简单：在最后一个 `public const string` 行后追加
        var m = Regex.Match(text, @"public const string \w+ = ""[^""]+"";\s*\r?\n", RegexOptions.RightToLeft);
        if (!m.Success)
        {
            notes.AppendLine("未能定位 ToolIds 插入点。");
            return;
        }
        text = text.Insert(m.Index + m.Length, insert);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        notes.AppendLine($"已写入 ToolIds.{constName}。打包后放入 Tools/Bundled 即可出现在工具页（不再维护硬编码 fallback）。");
    }

    private sealed class ProjectRefs
    {
        public required string Core { get; init; }
        public required string ToolSdk { get; init; }
        public required string Windows { get; init; }
        public required string ThemeWpf { get; init; }
        public required string ThemeMaui { get; init; }
        public required string Theme { get; init; }
    }

    private static ProjectRefs BuildProjectRefs(string projectFolder, string? repo)
    {
        // 逻辑工程目录：{folder}/FlyScreen.Tool.X/ → 相对到 src/
        static string Rel(string fromDir, string toFile)
        {
            var from = Path.GetFullPath(fromDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var to = Path.GetFullPath(toFile);
            return Path.GetRelativePath(from, to).Replace('/', '\\');
        }

        if (repo != null)
        {
            // 子工程在 projectFolder/<asm>/ 或 projectFolder/<asm>.Ui.*，深度相同
            var sampleChild = Path.Combine(projectFolder, "x");
            return new ProjectRefs
            {
                Core = Rel(sampleChild, Path.Combine(repo, "src", "FlyScreen.Core", "FlyScreen.Core.csproj")),
                ToolSdk = Rel(sampleChild, Path.Combine(repo, "src", "FlyScreen.ToolSdk", "FlyScreen.ToolSdk.csproj")),
                Windows = Rel(sampleChild, Path.Combine(repo, "src", "FlyScreen.Windows", "FlyScreen.Windows.csproj")),
                ThemeWpf = Rel(sampleChild, Path.Combine(repo, "src", "Theme", "FlyScreen.Ui.Theme.Wpf", "FlyScreen.Ui.Theme.Wpf.csproj")),
                ThemeMaui = Rel(sampleChild, Path.Combine(repo, "src", "Theme", "FlyScreen.Ui.Theme.Maui", "FlyScreen.Ui.Theme.Maui.csproj")),
                Theme = Rel(sampleChild, Path.Combine(repo, "src", "Theme", "FlyScreen.Ui.Theme", "FlyScreen.Ui.Theme.csproj"))
            };
        }

        // 无仓库：保留与 src/Tools/<Name>/ 对齐的相对路径（用户需把工程放进仓库）
        return new ProjectRefs
        {
            Core = @"..\..\..\FlyScreen.Core\FlyScreen.Core.csproj",
            ToolSdk = @"..\..\..\FlyScreen.ToolSdk\FlyScreen.ToolSdk.csproj",
            Windows = @"..\..\..\FlyScreen.Windows\FlyScreen.Windows.csproj",
            ThemeWpf = @"..\..\..\Theme\FlyScreen.Ui.Theme.Wpf\FlyScreen.Ui.Theme.Wpf.csproj",
            ThemeMaui = @"..\..\..\Theme\FlyScreen.Ui.Theme.Maui\FlyScreen.Ui.Theme.Maui.csproj",
            Theme = @"..\..\..\Theme\FlyScreen.Ui.Theme\FlyScreen.Ui.Theme.csproj"
        };
    }

    #region templates

    private static string BuildCoreCsproj(ToolScaffoldIdentity id, ProjectRefs r) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <RootNamespace>{{id.Namespace}}</RootNamespace>
            <AssemblyName>{{id.AssemblyName}}</AssemblyName>
            <Description>玄鸟工具：{{id.FolderName}}</Description>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="{{r.Core}}" />
            <ProjectReference Include="{{r.ToolSdk}}" />
          </ItemGroup>
          <ItemGroup>
            <EmbeddedResource Include="Icons\{{id.IconKey}}.png" LogicalName="{{id.Namespace}}.Icons.{{id.IconKey}}.png" />
          </ItemGroup>
        </Project>
        """;

    private static string BuildUiWinCsproj(ToolScaffoldIdentity id, ProjectRefs r) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows7.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <UseWPF>true</UseWPF>
            <RootNamespace>{{id.Namespace}}.Ui.Win</RootNamespace>
            <AssemblyName>{{id.AssemblyName}}.Ui.Win</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="..\{{id.AssemblyName}}\{{id.AssemblyName}}.csproj" />
            <ProjectReference Include="{{r.ToolSdk}}" />
            <ProjectReference Include="{{r.Core}}" />
            <ProjectReference Include="{{r.Windows}}" />
            <ProjectReference Include="{{r.ThemeWpf}}" />
          </ItemGroup>
        </Project>
        """;

    private static string BuildUiMauiCsproj(ToolScaffoldIdentity id, ProjectRefs r) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>net10.0;net10.0-windows10.0.19041.0</TargetFrameworks>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>{{id.Namespace}}.Ui.Maui</RootNamespace>
            <AssemblyName>{{id.AssemblyName}}.Ui.Maui</AssemblyName>
            <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'">10.0.17763.0</SupportedOSPlatformVersion>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.Maui.Controls" Version="10.0.20" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="..\{{id.AssemblyName}}\{{id.AssemblyName}}.csproj" />
            <ProjectReference Include="{{r.ToolSdk}}" />
            <ProjectReference Include="{{r.Core}}" />
            <ProjectReference Include="{{r.ThemeMaui}}" />
          </ItemGroup>
          <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'">
            <ProjectReference Include="{{r.Windows}}" />
          </ItemGroup>
        </Project>
        """;

    private static string BuildUiDesktopCsproj(ToolScaffoldIdentity id, ProjectRefs r) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>{{id.Namespace}}.Ui.Desktop</RootNamespace>
            <AssemblyName>{{id.AssemblyName}}.Ui.Desktop</AssemblyName>
            <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Avalonia" Version="11.3.23" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="{{r.Core}}" />
            <ProjectReference Include="{{r.ToolSdk}}" />
            <ProjectReference Include="{{r.Theme}}" />
            <ProjectReference Include="..\{{id.AssemblyName}}\{{id.AssemblyName}}.csproj" />
          </ItemGroup>
        </Project>
        """;

    private static string BuildManifest(ToolScaffoldRequest req, ToolScaffoldIdentity id)
    {
        var hasWs = req.UiMode == ToolUiMode.Workspace;
        var deviceClasses = new List<string>();
        var platforms = new List<string>();
        var features = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (req.SupportDesktop)
        {
            deviceClasses.Add("Desktop");
            platforms.AddRange(["Windows", "macOS", "Linux"]);
            features["Desktop"] = ["local"];
        }
        if (req.SupportPhone)
        {
            deviceClasses.Add("Phone");
            deviceClasses.Add("Tablet");
            if (!platforms.Contains("Android")) platforms.Add("Android");
            features["Phone"] = ["local"];
            features["Tablet"] = ["local"];
        }

        var dc = string.Join(", ", deviceClasses.Select(x => $"\"{x}\""));
        var pf = string.Join(", ", platforms.Distinct().Select(x => $"\"{x}\""));
        var fb = string.Join(",\n    ", features.Select(kv =>
            $"\"{kv.Key}\": [{string.Join(", ", kv.Value.Select(v => $"\"{v}\""))}]"));

        return $$"""
            {
              "id": "{{id.ToolId}}",
              "kind": "enhancement",
              "crossScreen": false,
              "group": "utilities",
              "nameKey": "{{id.NameKey}}",
              "summaryKey": "{{id.SummaryKey}}",
              "schemaVersion": 1,
              "minAppVersion": "1.0.0",
              "deviceClasses": [{{dc}}],
              "platforms": [{{pf}}],
              "featuresByDeviceClass": {
                {{fb}}
              },
              "roles": ["local"],
              "supportsRepair": false,
              "capNames": [],
              "titleBar": false,
              "hasWorkspace": {{(hasWs ? "true" : "false")}},
              "icon": "{{id.IconKey}}",
              "permissions": [],
              "package": {
                "version": "1.0.0",
                "format": "flytool",
                "formatVersion": 1
              },
              "entryAssembly": "{{id.AssemblyName}}.dll",
              "entryType": "{{id.Namespace}}.Module"
            }
            """;
    }

    private static string BuildModule(ToolScaffoldRequest req, ToolScaffoldIdentity id)
    {
        var usesWs = req.UiMode == ToolUiMode.Workspace ? "true" : "false";
        var primary = req.UiMode switch
        {
            ToolUiMode.Workspace => $$"""
                    if (ctx.Row.IsRunning)
                    {
                        if (!fromMenu)
                        {
                            OpenWorkspace();
                            return Task.FromResult((true, ""));
                        }
                        return StopInternalAsync();
                    }
                    if (!ctx.Row.CanStart)
                        return Task.FromResult((false, ""));
                    if (!_service.IsRunning)
                        _service.Start();
                    OpenWorkspace();
                    return Task.FromResult((true, ""));
            """,
            ToolUiMode.Dialog => $$"""
                    if (ctx.Row.IsRunning)
                    {
                        if (!fromMenu)
                            return ShowDialogAsync(ct);
                        return StopInternalAsync();
                    }
                    if (!ctx.Row.CanStart)
                        return Task.FromResult((false, ""));
                    if (!_service.IsRunning)
                        _service.Start();
                    return ShowDialogAsync(ct);
            """,
            _ => $$"""
                    if (ctx.Row.IsRunning)
                    {
                        if (!fromMenu)
                            return Task.FromResult((true, ""));
                        return StopInternalAsync();
                    }
                    if (!ctx.Row.CanStart)
                        return Task.FromResult((false, ""));
                    _service.Start();
                    return Task.FromResult((true, ""));
            """
        };

        var extras = req.UiMode switch
        {
            ToolUiMode.Workspace => $$"""
                public void OpenWorkspace()
                {
                    if (_host == null) return;
                    if (!_service.IsRunning)
                        _service.Start();
                    _host.Workspace.OpenOrFocus(Id, Loc.T("{{id.NameKey}}"));
                }
            """,
            ToolUiMode.Dialog => $$"""
                public const string UiMain = "main-dialog";

                private async Task<(bool Ok, string Message)> ShowDialogAsync(CancellationToken ct)
                {
                    if (_host?.Ui == null)
                        return (false, "UI 宿主未就绪");
                    await _host.Ui.RequestAsync(Id, UiMain, null, ct).ConfigureAwait(true);
                    return (true, "");
                }
            """,
            _ => ""
        };

        var stopExtra = req.UiMode == ToolUiMode.Workspace
            ? "_host?.Workspace.Close(Id);"
            : "";

        return $$"""
            using FlyScreen.Helpers.Localization;
            using FlyScreen.Services.Tools;
            using FlyScreen.ToolSdk;

            namespace {{id.Namespace}};

            /// <summary>
            /// {{ToolScaffoldNaming.EscapeCs(req.DisplayNameZh)}} —— .flytool 入口（manifest entryType）。
            /// 对接宿主生命周期与启动/停止；业务写在 <see cref="{{id.FolderName}}Service"/>。见 README.md、docs/ui-modes.md。
            /// </summary>
            public sealed class Module : IToolModule
            {
                public const string MenuStop = "stop";

                private readonly {{id.FolderName}}Service _service = new();
                private IToolHostServices? _host;

                static Module()
                {
                    {{id.FolderName}}Localization.EnsureRegistered();
                    {{id.FolderName}}Icons.EnsureRegistered();
                }

                public string Id => "{{id.ToolId}}";

                public bool IsRunning => _service.IsRunning;

                /// <summary>须与 manifest.hasWorkspace 一致。</summary>
                public bool UsesWorkspace => {{usesWs}};

                public {{id.FolderName}}Service Service => _service;

                public event Action? StateChanged
                {
                    add => _service.SessionChanged += value;
                    remove => _service.SessionChanged -= value;
                }

                public Task OnLoadedAsync(IToolHostServices host, CancellationToken ct = default)
                {
                    {{id.FolderName}}Localization.EnsureRegistered();
                    {{id.FolderName}}Icons.EnsureRegistered();
                    _host = host;
                    host.RegisterService(_service);
                    return Task.CompletedTask;
                }

                public Task OnUnloadedAsync(CancellationToken ct = default)
                {
                    _service.Stop();
                    {{stopExtra}}
                    try { _host?.UnregisterService<{{id.FolderName}}Service>(); } catch { /* ignore */ }
                    ToolIconRegistry.Unregister({{id.FolderName}}Icons.Instance.IconKey);
                    _host = null;
                    return Task.CompletedTask;
                }

                public Task<(bool Ok, string Message)> PrimaryAsync(
                    ToolCardContext ctx, bool fromMenu, CancellationToken ct = default)
                {
                    if (_host == null)
                        return Task.FromResult((false, "工具宿主未就绪"));

            {{primary}}
                }

                public Task StopAsync(CancellationToken ct = default)
                    => StopInternalAsync();

                public IReadOnlyList<ToolMenuContribution> GetMenuContributions(ToolCardContext ctx)
                {
                    if (!ctx.Row.IsInstalled) return [];
                    var list = new List<ToolMenuContribution>();
                    if (ctx.Row.IsRunning)
                        list.Add(new ToolMenuContribution { ActionId = MenuStop, Label = Loc.T("Tool.Stop") });
                    return list;
                }

                public Task InvokeMenuAsync(string actionId, CancellationToken ct = default)
                {
                    if (string.Equals(actionId, MenuStop, StringComparison.OrdinalIgnoreCase))
                        return StopInternalAsync();
                    return Task.CompletedTask;
                }

            {{extras}}

                private Task<(bool Ok, string Message)> StopInternalAsync()
                {
                    _service.Stop();
                    {{stopExtra}}
                    return Task.FromResult((true, ""));
                }
            }
            """;
    }

    private static string BuildService(ToolScaffoldRequest req, ToolScaffoldIdentity id)
    {
        var bgHint = req.UiMode == ToolUiMode.Background
            ? """
                // 纯后台：在此启动长任务，并用 CancellationToken 协作取消（见 docs/ui-modes.md §C）。
                // _cts = new CancellationTokenSource();
                // _loop = Task.Run(async () => { while (!_cts.Token.IsCancellationRequested) { ... } }, _cts.Token);
            """
            : "// 在此启动会话 / 初始化资源。";

        var bgStop = req.UiMode == ToolUiMode.Background
            ? """
                // _cts?.Cancel(); try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
                // _cts?.Dispose(); _cts = null; _loop = null;
            """
            : "// 在此释放资源、结束会话。";

        return $$"""
            namespace {{id.Namespace}};

            /// <summary>
            /// {{ToolScaffoldNaming.EscapeCs(req.DisplayNameZh)}} 业务服务（模板）。
            /// <para>卡片「运行中」状态来自 <see cref="IsRunning"/>；变更后触发 <see cref="SessionChanged"/> 刷新 UI。</para>
            /// <para>请在此实现核心逻辑；Module 只负责调用 Start/Stop 与打开 UI。</para>
            /// </summary>
            public sealed class {{id.FolderName}}Service
            {
                private int _running;
                // private CancellationTokenSource? _cts;
                // private Task? _loop;

                public bool IsRunning => Volatile.Read(ref _running) != 0;

                public event Action? SessionChanged;

                public void Start()
                {
                    if (Interlocked.Exchange(ref _running, 1) != 0) return;
                    {{bgHint}}
                    SessionChanged?.Invoke();
                }

                public void Stop()
                {
                    if (Interlocked.Exchange(ref _running, 0) == 0) return;
                    {{bgStop}}
                    SessionChanged?.Invoke();
                }
            }
            """;
    }

    private static string BuildLocalization(ToolScaffoldRequest req, ToolScaffoldIdentity id)
    {
        var zhName = ToolScaffoldNaming.EscapeCs(req.DisplayNameZh.Trim());
        var zhSum = ToolScaffoldNaming.EscapeCs(string.IsNullOrWhiteSpace(req.SummaryZh)
            ? req.DisplayNameZh.Trim()
            : req.SummaryZh.Trim());
        var enName = ToolScaffoldNaming.EscapeCs(id.FolderName);
        var enSum = ToolScaffoldNaming.EscapeCs(string.IsNullOrWhiteSpace(req.SummaryEn)
            ? (string.IsNullOrWhiteSpace(req.SummaryZh) ? id.FolderName : req.SummaryZh.Trim())
            : req.SummaryEn.Trim());

        return $$"""
            using FlyScreen.Helpers.Localization;
            using FlyScreen.ToolSdk;

            namespace {{id.Namespace}};

            public sealed class {{id.FolderName}}Localization : IToolLocalization
            {
                public static {{id.FolderName}}Localization Instance { get; } = new();

                static {{id.FolderName}}Localization() => EnsureRegistered();

                public string SourceId => "{{id.ToolId}}";

                public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; } =
                    ToolLocalizationBuilder.Build(Zh(), En(), Extras());

                public static void EnsureRegistered() => Loc.RegisterPlugin(Instance);

                private static Dictionary<string, string> Zh() => new(StringComparer.OrdinalIgnoreCase)
                {
                    ["{{id.NameKey}}"] = "{{zhName}}",
                    ["{{id.SummaryKey}}"] = "{{zhSum}}",
                    ["{{id.LocPrefix}}.Hint"] = "这是由开发者工具生成的模板，请在此实现业务逻辑。"
                };

                private static Dictionary<string, string> En() => new(StringComparer.OrdinalIgnoreCase)
                {
                    ["{{id.NameKey}}"] = "{{enName}}",
                    ["{{id.SummaryKey}}"] = "{{enSum}}",
                    ["{{id.LocPrefix}}.Hint"] = "Template generated by Developer Tools — implement your logic here."
                };

                /// <summary>Name/Summary stubs for major languages; builder fills remaining keys from en/zh.</summary>
                private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Extras()
                    => new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["zh-TW"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{zhName}}",
                            ["{{id.SummaryKey}}"] = "{{zhSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "這是由開發者工具產生的範本，請在此實作業務邏輯。"
                        },
                        ["ja-JP"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "開発者ツールが生成したテンプレートです。ここに業務ロジックを実装してください。"
                        },
                        ["ko-KR"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "개발자 도구가 생성한 템플릿입니다. 여기에 비즈니스 로직을 구현하세요."
                        },
                        ["fr-FR"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Modèle généré par Outils développeur — implémentez votre logique ici."
                        },
                        ["de-DE"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Von Entwicklertools erzeugte Vorlage — implementieren Sie hier Ihre Logik."
                        },
                        ["es-ES"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Plantilla generada por Herramientas de desarrollador — implemente su lógica aquí."
                        },
                        ["pt-BR"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Modelo gerado pelas Ferramentas do desenvolvedor — implemente sua lógica aqui."
                        },
                        ["ru-RU"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Шаблон от инструментов разработчика — реализуйте логику здесь."
                        },
                        ["ar-SA"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "قالب أنشأته أدوات المطوّر — نفّذ منطقك هنا."
                        },
                        ["hi-IN"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "डेवलपर टूल द्वारा जनरेट किया गया टेम्पलेट — यहाँ अपना लॉजिक लागू करें।"
                        },
                        ["th-TH"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "เทมเพลตจากเครื่องมือนักพัฒนา — ใส่ตรรกะธุรกิจที่นี่"
                        },
                        ["vi-VN"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Mẫu do Công cụ nhà phát triển tạo — triển khai logic tại đây."
                        },
                        ["id-ID"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Templat dari Alat pengembang — implementasikan logika Anda di sini."
                        },
                        ["tr-TR"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Geliştirici araçlarının oluşturduğu şablon — mantığınızı burada uygulayın."
                        },
                        ["it-IT"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Modello generato dagli Strumenti per sviluppatori — implementa la logica qui."
                        },
                        ["nl-NL"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Sjabloon van Ontwikkelaarshulpmiddelen — implementeer hier uw logica."
                        },
                        ["pl-PL"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Szablon z Narzędzi deweloperskich — zaimplementuj logikę tutaj."
                        },
                        ["uk-UA"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["{{id.NameKey}}"] = "{{enName}}",
                            ["{{id.SummaryKey}}"] = "{{enSum}}",
                            ["{{id.LocPrefix}}.Hint"] = "Шаблон від інструментів розробника — реалізуйте логіку тут."
                        }
                    };
            }
            """;
    }

    private static string BuildIcons(ToolScaffoldIdentity id) => $$"""
        using FlyScreen.ToolSdk;

        namespace {{id.Namespace}};

        public sealed class {{id.FolderName}}Icons : IToolIconContribution
        {
            public static {{id.FolderName}}Icons Instance { get; } = new();

            public string IconKey => "{{id.IconKey}}";

            public Stream? OpenBitmap()
            {
                const string name = "{{id.Namespace}}.Icons.{{id.IconKey}}.png";
                return typeof({{id.FolderName}}Icons).Assembly.GetManifestResourceStream(name);
            }

            public static void EnsureRegistered()
                => FlyScreen.Services.Tools.ToolIconRegistry.Register(Instance);
        }
        """;

    private static string BuildWinContributor(ToolScaffoldRequest req, ToolScaffoldIdentity id)
    {
        if (req.UiMode == ToolUiMode.Workspace)
        {
            return $$"""
                using FlyScreen.ToolSdk;

                namespace {{id.Namespace}}.Ui.Win;

                /// <summary>
                /// WPF UI 贡献：向宿主注册工作区面板工厂。
                /// 宿主扫描本程序集中的 <see cref="IToolUiContributor"/>，无需改主程序。
                /// </summary>
                public sealed class {{id.FolderName}}WinUiContributor : IToolUiContributor
                {
                    public void Register(IToolUiHost ui, IToolUiContext context)
                    {
                        // OpenOrFocus 时宿主调用工厂，把返回控件嵌入工具页工作区
                        ui.RegisterWorkspaceFactory("{{id.ToolId}}", () =>
                        {
                            var panel = new {{id.FolderName}}Panel();
                            if (context.ReportStatus != null)
                                panel.StatusReporter = context.ReportStatus;
                            return panel;
                        });
                    }
                }
                """;
        }

        return $$"""
            using System.Windows;
            using FlyScreen.ToolSdk;

            namespace {{id.Namespace}}.Ui.Win;

            /// <summary>
            /// WPF UI 贡献：注册弹窗 Handler。
            /// Module 通过 <c>Ui.RequestAsync(toolId, Module.UiMain, …)</c> 触发本 Handler。
            /// </summary>
            public sealed class {{id.FolderName}}WinUiContributor : IToolUiContributor
            {
                public void Register(IToolUiHost ui, IToolUiContext context)
                {
                    ui.RegisterHandler("{{id.ToolId}}", Module.UiMain, (arg, ct) =>
                    {
                        var owner = context.Owner as Window;
                        var dlg = new {{id.FolderName}}Dialog { Owner = owner };
                        dlg.ShowDialog();
                        return Task.FromResult<object?>(null);
                    });
                }
            }
            """;
    }

    private static string BuildWinPanelXaml(ToolScaffoldIdentity id) => $$"""
        <UserControl x:Class="{{id.Namespace}}.Ui.Win.{{id.FolderName}}Panel"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="16">
            <StackPanel>
              <TextBlock x:Name="HintText" TextWrapping="Wrap" Margin="0,0,0,12"/>
              <TextBlock Text="在此实现 {{id.FolderName}} 工作区 UI。" Opacity="0.7"/>
            </StackPanel>
          </ScrollViewer>
        </UserControl>
        """;

    private static string BuildWinPanelCode(ToolScaffoldIdentity id) => $$"""
        using System.Windows.Controls;
        using FlyScreen.Helpers.Localization;

        namespace {{id.Namespace}}.Ui.Win;

        /// <summary>嵌入工具页工作区的 WPF 面板 —— 在此实现主界面。</summary>
        public partial class {{id.FolderName}}Panel : UserControl
        {
            public Action<string>? StatusReporter { get; set; }

            public {{id.FolderName}}Panel()
            {
                InitializeComponent();
                HintText.Text = Loc.T("{{id.LocPrefix}}.Hint");
            }
        }
        """;

    private static string BuildWinDialogXaml(ToolScaffoldIdentity id) => $$"""
        <Window x:Class="{{id.Namespace}}.Ui.Win.{{id.FolderName}}Dialog"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="{{id.FolderName}}" Height="320" Width="480"
                WindowStartupLocation="CenterOwner">
          <Grid Margin="16">
            <TextBlock x:Name="HintText" TextWrapping="Wrap" VerticalAlignment="Center"/>
          </Grid>
        </Window>
        """;

    private static string BuildWinDialogCode(ToolScaffoldIdentity id) => $$"""
        using System.Windows;
        using FlyScreen.Helpers.Localization;

        namespace {{id.Namespace}}.Ui.Win;

        /// <summary>启动弹窗（非工作区）。由 WinUiContributor 的 RegisterHandler 弹出。</summary>
        public partial class {{id.FolderName}}Dialog : Window
        {
            public {{id.FolderName}}Dialog()
            {
                InitializeComponent();
                HintText.Text = Loc.T("{{id.LocPrefix}}.Hint");
            }
        }
        """;

    private static string BuildMauiTheme(ToolScaffoldIdentity id) => $$"""
        namespace {{id.Namespace}}.Ui.Maui;

        internal static class ToolMauiTheme
        {
            public static bool IsDark
                => Application.Current?.UserAppTheme == AppTheme.Dark
                   || Application.Current?.RequestedTheme == AppTheme.Dark;
        }
        """;

    private static string BuildMauiContributor(ToolScaffoldRequest req, ToolScaffoldIdentity id)
    {
        if (req.UiMode == ToolUiMode.Workspace)
        {
            return $$"""
                using FlyScreen.ToolSdk;

                namespace {{id.Namespace}}.Ui.Maui;

                public sealed class {{id.FolderName}}MauiUiContributor : IToolUiContributor
                {
                    public void Register(IToolUiHost ui, IToolUiContext context)
                    {
                        ui.RegisterWorkspaceFactory("{{id.ToolId}}", () =>
                        {
                            var panel = new {{id.FolderName}}Panel();
                            if (context.ReportStatus != null)
                                panel.StatusReporter = context.ReportStatus;
                            return panel;
                        });
                    }
                }
                """;
        }

        return $$"""
            using FlyScreen.ToolSdk;

            namespace {{id.Namespace}}.Ui.Maui;

            public sealed class {{id.FolderName}}MauiUiContributor : IToolUiContributor
            {
                public void Register(IToolUiHost ui, IToolUiContext context)
                {
                    ui.RegisterHandler("{{id.ToolId}}", Module.UiMain, async (arg, ct) =>
                    {
                        var nav = context.GetNavigation?.Invoke() as INavigation;
                        if (nav != null)
                            await nav.PushModalAsync(new {{id.FolderName}}DialogPage()).ConfigureAwait(true);
                        return null;
                    });
                }
            }
            """;
    }

    private static string BuildMauiPanel(ToolScaffoldIdentity id) => $$"""
        using FlyScreen.Helpers.Localization;

        namespace {{id.Namespace}}.Ui.Maui;

        public sealed class {{id.FolderName}}Panel : ContentView
        {
            public Action<string>? StatusReporter { get; set; }

            public {{id.FolderName}}Panel()
            {
                var dark = ToolMauiTheme.IsDark;
                var muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
                Content = new ScrollView
                {
                    Content = new VerticalStackLayout
                    {
                        Padding = 16,
                        Spacing = 12,
                        Children =
                        {
                            new Label
                            {
                                Text = Loc.T("{{id.LocPrefix}}.Hint"),
                                FontSize = 13,
                                TextColor = muted,
                                LineBreakMode = LineBreakMode.WordWrap
                            },
                            new Label
                            {
                                Text = "在此实现 {{id.FolderName}} 工作区 UI。",
                                FontSize = 14
                            }
                        }
                    }
                };
            }
        }
        """;

    private static string BuildMauiDialog(ToolScaffoldIdentity id) => $$"""
        using FlyScreen.Helpers.Localization;

        namespace {{id.Namespace}}.Ui.Maui;

        public sealed class {{id.FolderName}}DialogPage : ContentPage
        {
            public {{id.FolderName}}DialogPage()
            {
                Title = Loc.T("{{id.NameKey}}");
                Content = new VerticalStackLayout
                {
                    Padding = 16,
                    Children =
                    {
                        new Label { Text = Loc.T("{{id.LocPrefix}}.Hint"), LineBreakMode = LineBreakMode.WordWrap },
                        new Button
                        {
                            Text = "关闭",
                            Margin = new Thickness(0, 16, 0, 0),
                            Command = new Command(async () => await Navigation.PopModalAsync())
                        }
                    }
                };
            }
        }
        """;

    private static string BuildDesktopContributor(ToolScaffoldRequest req, ToolScaffoldIdentity id)
    {
        if (req.UiMode == ToolUiMode.Workspace)
        {
            return $$"""
                using FlyScreen.ToolSdk;

                namespace {{id.Namespace}}.Ui.Desktop;

                public sealed class {{id.FolderName}}DesktopUiContributor : IToolUiContributor
                {
                    public void Register(IToolUiHost ui, IToolUiContext context)
                    {
                        ui.RegisterWorkspaceFactory("{{id.ToolId}}", () =>
                        {
                            var panel = new {{id.FolderName}}Panel();
                            if (context.ReportStatus != null)
                                panel.StatusReporter = context.ReportStatus;
                            return panel;
                        });
                    }
                }
                """;
        }

        return $$"""
            using Avalonia.Controls;
            using FlyScreen.ToolSdk;

            namespace {{id.Namespace}}.Ui.Desktop;

            public sealed class {{id.FolderName}}DesktopUiContributor : IToolUiContributor
            {
                public void Register(IToolUiHost ui, IToolUiContext context)
                {
                    ui.RegisterHandler("{{id.ToolId}}", Module.UiMain, async (arg, ct) =>
                    {
                        var owner = context.Owner as Window;
                        var dlg = new {{id.FolderName}}DialogWindow();
                        if (owner != null)
                            await dlg.ShowDialog(owner).ConfigureAwait(true);
                        else
                            dlg.Show();
                        return null;
                    });
                }
            }
            """;
    }

    private static string BuildDesktopPanelAxaml(ToolScaffoldIdentity id) => $$"""
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     x:Class="{{id.Namespace}}.Ui.Desktop.{{id.FolderName}}Panel">
          <ScrollViewer Padding="16">
            <StackPanel Spacing="12">
              <TextBlock x:Name="HintText" TextWrapping="Wrap"/>
              <TextBlock Text="在此实现 {{id.FolderName}} 工作区 UI。" Opacity="0.7"/>
            </StackPanel>
          </ScrollViewer>
        </UserControl>
        """;

    private static string BuildDesktopPanelCode(ToolScaffoldIdentity id) => $$"""
        using Avalonia.Controls;
        using FlyScreen.Helpers.Localization;

        namespace {{id.Namespace}}.Ui.Desktop;

        public partial class {{id.FolderName}}Panel : UserControl
        {
            public Action<string>? StatusReporter { get; set; }

            public {{id.FolderName}}Panel()
            {
                InitializeComponent();
                HintText.Text = Loc.T("{{id.LocPrefix}}.Hint");
            }
        }
        """;

    private static string BuildDesktopDialogAxaml(ToolScaffoldIdentity id) => $$"""
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="{{id.Namespace}}.Ui.Desktop.{{id.FolderName}}DialogWindow"
                Title="{{id.FolderName}}" Width="480" Height="320"
                WindowStartupLocation="CenterOwner">
          <TextBlock x:Name="HintText" Margin="16" TextWrapping="Wrap"/>
        </Window>
        """;

    private static string BuildDesktopDialogCode(ToolScaffoldIdentity id) => $$"""
        using Avalonia.Controls;
        using FlyScreen.Helpers.Localization;

        namespace {{id.Namespace}}.Ui.Desktop;

        public partial class {{id.FolderName}}DialogWindow : Window
        {
            public {{id.FolderName}}DialogWindow()
            {
                InitializeComponent();
                HintText.Text = Loc.T("{{id.LocPrefix}}.Hint");
            }
        }
        """;

    private static void WritePlaceholderPng(string path)
    {
        // 1x1 PNG
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO5W2fQAAAAASUVORK5CYII=");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    #endregion
}
