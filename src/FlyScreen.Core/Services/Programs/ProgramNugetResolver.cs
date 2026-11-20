using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace FlyScreen.Services.Programs;

public sealed class ProgramNugetResolveResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];
}

/// <summary>
/// 桌面：把 Packages 写入 program.csproj 并 dotnet restore，解析编译用 DLL。
/// 手机：不调用 restore；由调用方改用 LocalDlls。
/// </summary>
public static class ProgramNugetResolver
{
    public const string FlyRefsCacheFile = "obj/fly-refs.txt";

    /// <summary>与 <c>FlyScreen.Core.csproj</c> 中 Roslyn 包版本保持一致（DevHost 运行时编译依赖）。</summary>
    public const string RoslynPackageVersion = "4.14.0";

    public static bool CanRestoreOnline
        => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    public static string BuildCsproj(IReadOnlyList<NugetPackageRef>? packages)
    {
        var sb = new StringBuilder();
        // DevHost 仅编译外部入口；main.cs / lib 由 ProgramScriptHost 在运行时 Wrap+编译。
        // 引用安装目录 / 宿主 exe 旁的 FlyScreen.Core.dll（禁止 ProjectReference 源码工程）。
        // Directory.Build.props：FlyScreenHostDir / FlyScreenCoreDll；或环境变量 FLYSCREEN_HOST_DIR。
        // Roslyn 必须用 PackageReference 拉进 deps.json：裸 Reference Core 不会传递 CodeAnalysis，
        // 且勿再把宿主整目录塞进 refs/（原生 WinAppSDK DLL 会触发 CS0009）。
        sb.AppendLine($$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <RootNamespace>FlyScreen.ProgramDevHost</RootNamespace>
                <AssemblyName>program</AssemblyName>
                <FlyScreenHostDir Condition="'$(FlyScreenHostDir)' == '' and '$(FLYSCREEN_HOST_DIR)' != ''">$(FLYSCREEN_HOST_DIR)</FlyScreenHostDir>
                <FlyScreenCoreDll Condition="'$(FlyScreenCoreDll)' == '' and '$(FlyScreenHostDir)' != ''">$(FlyScreenHostDir)\FlyScreen.Core.dll</FlyScreenCoreDll>
                <FlyScreenCoreDll Condition="'$(FlyScreenCoreDll)' == '' and '$(FLYSCREEN_CORE_DLL)' != ''">$(FLYSCREEN_CORE_DLL)</FlyScreenCoreDll>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="DevHost.cs" />
                <!-- 供 IDE 浏览；不参与本项目编译（由 DevHost → ProgramScriptHost 加载） -->
                <None Include="main.cs" />
                <None Include="lib\**\*.cs" />
                <None Include="fly-settings.json" Condition="Exists('fly-settings.json')" />
                <None Include="program.json" Condition="Exists('program.json')" />
                <None Include="settings.json" Condition="Exists('settings.json')" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="{{RoslynPackageVersion}}" />
              </ItemGroup>
              <ItemGroup Condition="'$(FlyScreenCoreDll)' != '' and Exists('$(FlyScreenCoreDll)')">
                <Reference Include="FlyScreen.Core">
                  <HintPath>$(FlyScreenCoreDll)</HintPath>
                  <!-- 必须 CopyLocal：HintPath 只参与编译，Private=false 时运行会找不到 Core -->
                  <Private>true</Private>
                </Reference>
              </ItemGroup>
              <!-- 宿主旁 DLL → 输出（Jint/Assets 等）；Roslyn 优先走上面 PackageReference -->
              <Target Name="FlyScreen_CopyHostDeps" AfterTargets="Build"
                      Condition="'$(FlyScreenHostDir)' != '' and Exists('$(FlyScreenHostDir)')">
                <ItemGroup>
                  <_FlyHostDll Include="$(FlyScreenHostDir)\*.dll" />
                </ItemGroup>
                <Copy SourceFiles="@(_FlyHostDll)"
                      DestinationFolder="$(OutputPath)"
                      SkipUnchangedFiles="true"
                      Condition="'@(_FlyHostDll)' != ''" />
              </Target>
            """);
        var pkgs = (packages ?? [])
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        if (pkgs.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var p in pkgs)
            {
                var id = EscapeXml(p.Id.Trim());
                var ver = string.IsNullOrWhiteSpace(p.Version) ? "*" : EscapeXml(p.Version.Trim());
                sb.AppendLine($"""    <PackageReference Include="{id}" Version="{ver}" />""");
            }
            sb.AppendLine("  </ItemGroup>");
        }
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    /// <summary>外部 VS / dotnet run 用的薄入口（写入程序目录 <c>DevHost.cs</c>）。</summary>
    public static string BuildDevHostSource()
        => """
            using FlyScreen.Services.Programs;

            // 外部开发入口：dotnet run / VS F5。
            // 玄鸟内运行仍走进程内 Roslyn，不会编译本文件（勿加入 program.json ExtraFiles）。
            internal static class Program
            {
                private static async Task<int> Main(string[] args)
                {
                    var folder = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
                        ? Path.GetFullPath(args[0])
                        : FindProgramFolder();

                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        cts.Cancel();
                    };

                    return await ProgramExternalDevHost.RunAsync(folder, cts.Token).ConfigureAwait(false);
                }

                private static string FindProgramFolder()
                {
                    // bin/Debug/netX.0 → 程序根（含 main.cs）
                    var dir = new DirectoryInfo(AppContext.BaseDirectory);
                    for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent!)
                    {
                        if (File.Exists(Path.Combine(dir.FullName, "main.cs"))
                            || File.Exists(Path.Combine(dir.FullName, "program.json")))
                            return dir.FullName;
                    }
                    return Directory.GetCurrentDirectory();
                }
            }
            """;

    public static void SyncCsproj(ProgramCatalog catalog, UserProgram prog)
    {
        if (prog.Language != ProgramLanguage.CSharp) return;
        catalog.NormalizeManifest(prog);
        var folder = catalog.GetFolder(prog);
        ProgramExternalDevScaffold.Ensure(folder, prog.Language, prog.Packages);
        ProgramUiScaffold.Apply(folder, prog);
    }

    /// <summary>
    /// 桌面 restore 并返回引用 DLL；无包时返回空列表（成功）。
    /// 手机直接返回失败提示（若 Packages 非空）。
    /// </summary>
    public static async Task<ProgramNugetResolveResult> ResolveAsync(
        ProgramCatalog catalog, UserProgram prog, CancellationToken ct = default)
    {
        catalog.NormalizeManifest(prog);
        if (prog.Packages.Count == 0)
            return new ProgramNugetResolveResult { Success = true, ReferencePaths = [] };

        if (!CanRestoreOnline)
        {
            return new ProgramNugetResolveResult
            {
                Success = false,
                Error = "手机端不支持在线 NuGet。请在电脑还原依赖，或把所需 DLL 放入程序目录 refs/ 并登记为本地引用。"
            };
        }

        try
        {
            SyncCsproj(catalog, prog);
            var folder = catalog.GetFolder(prog);
            var restore = await RunDotnetAsync(folder, "restore program.csproj --nologo", ct)
                .ConfigureAwait(false);
            if (restore.ExitCode != 0)
            {
                return new ProgramNugetResolveResult
                {
                    Success = false,
                    Error = "dotnet restore 失败：\n" + TrimOutput(restore.Output)
                };
            }

            var refs = CollectPackageDlls(folder, prog.Packages);
            WriteRefsCache(folder, refs);
            return new ProgramNugetResolveResult
            {
                Success = true,
                ReferencePaths = refs
            };
        }
        catch (Exception ex)
        {
            return new ProgramNugetResolveResult
            {
                Success = false,
                Error = "NuGet 解析失败: " + ex.Message
            };
        }
    }

    public static IReadOnlyList<string> TryReadRefsCache(ProgramCatalog catalog, UserProgram prog)
    {
        try
        {
            var path = Path.Combine(catalog.GetFolder(prog), FlyRefsCacheFile);
            if (!File.Exists(path)) return [];
            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && File.Exists(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void WriteRefsCache(string folder, IReadOnlyList<string> refs)
    {
        var path = Path.Combine(folder, FlyRefsCacheFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, refs, new UTF8Encoding(false));
    }

    private static List<string> CollectPackageDlls(string folder, IReadOnlyList<NugetPackageRef> packages)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nugetRoots = EnumerateNugetRoots(folder);
        foreach (var pkg in packages)
        {
            if (string.IsNullOrWhiteSpace(pkg.Id)) continue;
            foreach (var root in nugetRoots)
            {
                var pkgDir = FindPackageDir(root, pkg.Id.Trim(), pkg.Version);
                if (pkgDir == null) continue;
                foreach (var dll in EnumerateLibDlls(pkgDir))
                {
                    if (!seen.Add(dll)) continue;
                    // 跳过包内偶发的原生 PE，避免飞进 Roslyn 后 CS0009
                    if (!ProgramScriptHost.HasManagedMetadata(dll))
                        continue;
                    result.Add(dll);
                }
            }
        }
        return result;
    }

    private static IEnumerable<string> EnumerateNugetRoots(string projectFolder)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p);
                if (Directory.Exists(full)) set.Add(full);
            }
            catch { /* ignore */ }
        }

        Add(Path.Combine(projectFolder, "nuget-packages"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(home, ".nuget", "packages"));
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        Add(env);
        return set;
    }

    private static string? FindPackageDir(string nugetRoot, string id, string? version)
    {
        var idDir = Path.Combine(nugetRoot, id.ToLowerInvariant());
        if (!Directory.Exists(idDir))
        {
            // 兼容大小写
            idDir = Directory.EnumerateDirectories(nugetRoot)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), id, StringComparison.OrdinalIgnoreCase));
            if (idDir == null) return null;
        }

        if (!string.IsNullOrWhiteSpace(version) && version != "*")
        {
            var exact = Path.Combine(idDir, version.Trim());
            if (Directory.Exists(exact)) return exact;
            var match = Directory.EnumerateDirectories(idDir)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), version.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return Directory.EnumerateDirectories(idDir)
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateLibDlls(string packageVersionDir)
    {
        var lib = Path.Combine(packageVersionDir, "lib");
        if (!Directory.Exists(lib)) yield break;

        // 优先 net10 / net9 / net8 / netstandard2.x
        string[] prefer =
        [
            "net10.0", "net9.0", "net8.0", "net7.0", "net6.0",
            "netstandard2.1", "netstandard2.0", "netstandard1.6", "netstandard1.3"
        ];
        foreach (var tfm in prefer)
        {
            var dir = Path.Combine(lib, tfm);
            if (!Directory.Exists(dir)) continue;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                yield return dll;
            yield break;
        }

        foreach (var dll in Directory.EnumerateFiles(lib, "*.dll", SearchOption.AllDirectories))
            yield return dll;
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(
        string workDir, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 dotnet");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, (stdout + "\n" + stderr).Trim());
    }

    private static string TrimOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "(无输出)";
        var lines = output.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= 40) return output.Trim();
        return string.Join('\n', lines.Take(20).Concat(["…"]).Concat(lines.TakeLast(20)));
    }

    private static string EscapeXml(string s)
        => System.Security.SecurityElement.Escape(s) ?? s;

    /// <summary>从已有 csproj 读取 PackageReference（迁移/同步用）。</summary>
    public static List<NugetPackageRef> ReadPackagesFromCsproj(string csprojPath)
    {
        var list = new List<NugetPackageRef>();
        if (!File.Exists(csprojPath)) return list;
        try
        {
            var doc = XDocument.Load(csprojPath);
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var id = (string?)el.Attribute("Include") ?? (string?)el.Attribute("Update");
                var ver = (string?)el.Attribute("Version")
                          ?? el.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new NugetPackageRef { Id = id.Trim(), Version = (ver ?? "").Trim() });
            }
        }
        catch
        {
            // ignore malformed
        }
        return list;
    }
}
