using System.Text;

namespace FlyScreen.Services.Programs;

/// <summary>按 <see cref="ProgramUiKind"/> 生成 UI 脚手架（XAML / UiHost / 专用 csproj）。</summary>
public static class ProgramUiScaffold
{
    public static void Apply(string programFolder, UserProgram prog)
    {
        if (prog.Language != ProgramLanguage.CSharp || prog.UiKind == ProgramUiKind.None)
            return;
        if (string.IsNullOrWhiteSpace(programFolder) || !Directory.Exists(programFolder))
            return;

        Directory.CreateDirectory(Path.Combine(programFolder, "lib"));
        switch (prog.UiKind)
        {
            case ProgramUiKind.Wpf:
                ApplyWpf(programFolder, prog);
                break;
            case ProgramUiKind.Avalonia:
                ApplyAvalonia(programFolder, prog);
                break;
            case ProgramUiKind.Maui:
                ApplyMaui(programFolder, prog);
                break;
        }
    }

    public static void Apply(ProgramCatalog catalog, UserProgram prog)
        => Apply(catalog.GetFolder(prog), prog);

    private static void ApplyWpf(string folder, UserProgram prog)
    {
        var fresh = !File.Exists(Path.Combine(folder, "MainWindow.xaml"));
        if (fresh)
        {
            WriteUtf8(Path.Combine(folder, "MainWindow.xaml"), WpfMainWindowXaml);
            WriteUtf8(Path.Combine(folder, "MainWindow.xaml.cs"), WpfMainWindowCodeBehind);
            WriteUtf8(Path.Combine(folder, prog.FileName), BuildMainWithUi(prog, "UiHost.ShowMainWindowAsync(ct)"));
        }
        WriteUtf8IfMissing(Path.Combine(folder, "lib", "UiHost.cs"), WpfUiHostSource);
        WriteUtf8(Path.Combine(folder, "program.csproj"), BuildWpfCsproj(prog.Packages));
        WriteUtf8(Path.Combine(folder, "DevHost.cs"), BuildWpfDevHostSource());
        RegisterExtra(prog, "lib/UiHost.cs");
    }

    private static void ApplyAvalonia(string folder, UserProgram prog)
    {
        var fresh = !File.Exists(Path.Combine(folder, "MainWindow.axaml"));
        if (fresh)
        {
            WriteUtf8(Path.Combine(folder, "MainWindow.axaml"), AvaloniaMainWindowXaml);
            WriteUtf8(Path.Combine(folder, "MainWindow.axaml.cs"), AvaloniaMainWindowCodeBehind);
            WriteUtf8(Path.Combine(folder, prog.FileName), BuildMainWithUi(prog, "UiHost.ShowMainWindowAsync(ct)"));
        }
        WriteUtf8IfMissing(Path.Combine(folder, "lib", "UiHost.cs"), AvaloniaUiHostSource);
        WriteUtf8(Path.Combine(folder, "program.csproj"), BuildAvaloniaCsproj(prog.Packages));
        RegisterExtra(prog, "lib/UiHost.cs");
    }

    private static void ApplyMaui(string folder, UserProgram prog)
    {
        var fresh = !File.Exists(Path.Combine(folder, "ProgramUiPage.xaml"));
        if (fresh)
        {
            WriteUtf8(Path.Combine(folder, "ProgramUiPage.xaml"), MauiPageXaml);
            WriteUtf8(Path.Combine(folder, "ProgramUiPage.xaml.cs"), MauiPageCodeBehind);
            WriteUtf8(Path.Combine(folder, prog.FileName), BuildMainWithUi(prog, "UiHost.ShowMainPageAsync(ct)"));
        }
        WriteUtf8IfMissing(Path.Combine(folder, "lib", "UiHost.cs"), MauiUiHostSource);
        RegisterExtra(prog, "lib/UiHost.cs");
    }

    private static void RegisterExtra(UserProgram prog, string relativePath)
    {
        prog.ExtraFiles ??= [];
        var rel = ProgramCatalog.NormalizeRelativePath(relativePath);
        if (!prog.ExtraFiles.Any(f => string.Equals(f, rel, StringComparison.OrdinalIgnoreCase)))
            prog.ExtraFiles.Add(rel);
    }

    private static string BuildMainWithUi(UserProgram prog, string showCall)
        => $$"""
            // namespace: {{prog.Namespace}}
            // name: {{prog.Name}}
            using FlyScreen.ProgramDevHost;

            Console.WriteLine("Hello from {{prog.Name}}");

            var result = await {{showCall}};
            Console.WriteLine($"UI result = {result}");
            """;

    private static void WriteUtf8(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteUtf8IfMissing(string path, string content)
    {
        if (File.Exists(path)) return;
        WriteUtf8(path, content);
    }

    private static string BuildWpfCsproj(IReadOnlyList<NugetPackageRef>? packages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows7.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <GenerateAssemblyInfo>true</GenerateAssemblyInfo>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>
                <EnableDefaultPageItems>false</EnableDefaultPageItems>
                <RootNamespace>FlyScreen.ProgramDevHost</RootNamespace>
                <AssemblyName>program</AssemblyName>
                <StartupObject>Program</StartupObject>
                <UseWPF>true</UseWPF>
                <FlyScreenHostDir Condition="'$(FlyScreenHostDir)' == '' and '$(FLYSCREEN_HOST_DIR)' != ''">$(FLYSCREEN_HOST_DIR)</FlyScreenHostDir>
                <FlyScreenCoreDll Condition="'$(FlyScreenCoreDll)' == '' and '$(FlyScreenHostDir)' != ''">$(FlyScreenHostDir)\FlyScreen.Core.dll</FlyScreenCoreDll>
                <FlyScreenCoreDll Condition="'$(FlyScreenCoreDll)' == '' and '$(FLYSCREEN_CORE_DLL)' != ''">$(FLYSCREEN_CORE_DLL)</FlyScreenCoreDll>
              </PropertyGroup>
              <ItemGroup>
                <None Remove="**/*.xaml" />
                <Page Include="MainWindow.xaml">
                  <Generator>MSBuild:Compile</Generator>
                  <SubType>Designer</SubType>
                </Page>
                <Compile Include="MainWindow.xaml.cs">
                  <DependentUpon>MainWindow.xaml</DependentUpon>
                  <SubType>Code</SubType>
                </Compile>
                <Compile Include="DevHost.cs" />
                <None Include="main.cs" />
                <None Include="lib\**\*.cs" />
                <None Include="program.json" Condition="Exists('program.json')" />
                <None Include="settings.json" Condition="Exists('settings.json')" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="{{ProgramNugetResolver.RoslynPackageVersion}}" />
              </ItemGroup>
              <ItemGroup Condition="'$(FlyScreenCoreDll)' != '' and Exists('$(FlyScreenCoreDll)')">
                <Reference Include="FlyScreen.Core">
                  <HintPath>$(FlyScreenCoreDll)</HintPath>
                  <Private>true</Private>
                </Reference>
              </ItemGroup>
              <Target Name="FlyScreen_CopyHostDeps" AfterTargets="Build"
                      Condition="'$(FlyScreenHostDir)' != '' and Exists('$(FlyScreenHostDir)')">
                <ItemGroup>
                  <_FlyHostDll Include="$(FlyScreenHostDir)\*.dll" />
                </ItemGroup>
                <Copy SourceFiles="@(_FlyHostDll)" DestinationFolder="$(OutputPath)" SkipUnchangedFiles="true"
                      Condition="'@(_FlyHostDll)' != ''" />
              </Target>
            """);
        AppendUserPackages(sb, packages);
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private static string BuildAvaloniaCsproj(IReadOnlyList<NugetPackageRef>? packages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
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
                <None Include="main.cs" />
                <None Include="lib\**\*.cs" />
                <None Include="MainWindow.axaml" />
                <None Include="MainWindow.axaml.cs" />
                <None Include="program.json" Condition="Exists('program.json')" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="{{ProgramNugetResolver.RoslynPackageVersion}}" />
                <PackageReference Include="Avalonia" Version="11.3.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(FlyScreenCoreDll)' != '' and Exists('$(FlyScreenCoreDll)')">
                <Reference Include="FlyScreen.Core">
                  <HintPath>$(FlyScreenCoreDll)</HintPath>
                  <Private>true</Private>
                </Reference>
              </ItemGroup>
              <Target Name="FlyScreen_CopyHostDeps" AfterTargets="Build"
                      Condition="'$(FlyScreenHostDir)' != '' and Exists('$(FlyScreenHostDir)')">
                <ItemGroup>
                  <_FlyHostDll Include="$(FlyScreenHostDir)\*.dll" />
                </ItemGroup>
                <Copy SourceFiles="@(_FlyHostDll)" DestinationFolder="$(OutputPath)" SkipUnchangedFiles="true"
                      Condition="'@(_FlyHostDll)' != ''" />
              </Target>
            """);
        AppendUserPackages(sb, packages);
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private static void AppendUserPackages(StringBuilder sb, IReadOnlyList<NugetPackageRef>? packages)
    {
        var pkgs = (packages ?? [])
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        if (pkgs.Count == 0) return;
        sb.AppendLine("  <ItemGroup>");
        foreach (var p in pkgs)
        {
            var id = System.Security.SecurityElement.Escape(p.Id.Trim()) ?? p.Id.Trim();
            var ver = string.IsNullOrWhiteSpace(p.Version) ? "*" : (System.Security.SecurityElement.Escape(p.Version.Trim()) ?? "*");
            sb.AppendLine($"""    <PackageReference Include="{id}" Version="{ver}" />""");
        }
        sb.AppendLine("  </ItemGroup>");
    }

    private static string BuildWpfDevHostSource()
        => """
            using FlyScreen.Services.Programs;

            // 外部开发入口：dotnet run / VS F5（WPF UI 子程序）。
            // 玄鸟内运行仍走进程内 Roslyn + lib/UiHost 进程外开窗。
            internal static class Program
            {
                [STAThread]
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

    private const string WpfMainWindowXaml =
        """
        <Window x:Class="FlyScreen.ProgramDevHost.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="FlyScreen Program" Height="360" Width="520"
                WindowStartupLocation="CenterScreen">
          <Grid Margin="16">
            <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
              <TextBlock Text="Hello from WPF UI" FontSize="20" Margin="0,0,0,12"/>
              <Button Content="关闭" Width="120" Height="32" IsCancel="True" IsDefault="True"
                      Click="Close_Click"/>
            </StackPanel>
          </Grid>
        </Window>
        """;

    private const string WpfMainWindowCodeBehind =
        """
        using System.Windows;

        namespace FlyScreen.ProgramDevHost;

        public partial class MainWindow : Window
        {
            public MainWindow()
            {
                InitializeComponent();
            }

            private void Close_Click(object sender, RoutedEventArgs e) => Close();
        }
        """;

    /// <summary>与样板一致的进程外 WPF UiHost（精简版，嵌入脚手架）。</summary>
    private static string WpfUiHostSource => EmbeddedWpfUiHost;

    private const string AvaloniaMainWindowXaml =
        """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="FlyScreen.ProgramDevHost.MainWindow"
                Title="FlyScreen Program" Width="520" Height="360"
                WindowStartupLocation="CenterScreen">
          <StackPanel Margin="16" VerticalAlignment="Center" HorizontalAlignment="Center" Spacing="12">
            <TextBlock Text="Hello from Avalonia UI" FontSize="20"/>
            <Button Content="关闭" Width="120" Height="32" Click="Close_Click"/>
          </StackPanel>
        </Window>
        """;

    private const string AvaloniaMainWindowCodeBehind =
        """
        using Avalonia.Controls;
        using Avalonia.Interactivity;

        namespace FlyScreen.ProgramDevHost;

        public partial class MainWindow : Window
        {
            public MainWindow()
            {
                InitializeComponent();
            }

            private void Close_Click(object? sender, RoutedEventArgs e) => Close();
        }
        """;

    private const string AvaloniaUiHostSource =
        """
        using System.Diagnostics;
        using System.Text;

        namespace FlyScreen.ProgramDevHost;

        /// <summary>Avalonia：优先走宿主 IProgramUiHost；否则提示需在 Avalonia 玄鸟中运行。</summary>
        public static class UiHost
        {
            public static async Task<bool?> ShowMainWindowAsync(CancellationToken token)
            {
                var host = FlyScreen.Services.Programs.ProgramUiHost.Current;
                if (host != null)
                    return await host.ShowAsync("MainWindow.axaml", token).ConfigureAwait(false);

                Console.WriteLine("未注册 IProgramUiHost：请在 Mac/Linux Avalonia 玄鸟内运行本程序。");
                return null;
            }
        }
        """;

    private const string MauiPageXaml =
        """
        <?xml version="1.0" encoding="utf-8" ?>
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                     x:Class="FlyScreen.ProgramDevHost.ProgramUiPage"
                     Title="FlyScreen Program">
          <VerticalStackLayout Padding="24" Spacing="16" VerticalOptions="Center">
            <Label Text="Hello from MAUI UI" FontSize="20" HorizontalOptions="Center"/>
            <Button Text="关闭" WidthRequest="120" Clicked="Close_Clicked" HorizontalOptions="Center"/>
          </VerticalStackLayout>
        </ContentPage>
        """;

    private const string MauiPageCodeBehind =
        """
        namespace FlyScreen.ProgramDevHost;

        public partial class ProgramUiPage : ContentPage
        {
            public ProgramUiPage()
            {
                InitializeComponent();
            }

            private async void Close_Clicked(object? sender, EventArgs e)
            {
                if (Navigation.NavigationStack.Count > 1)
                    await Navigation.PopAsync();
                else if (Navigation.ModalStack.Count > 0)
                    await Navigation.PopModalAsync();
            }
        }
        """;

    private const string MauiUiHostSource =
        """
        namespace FlyScreen.ProgramDevHost;

        /// <summary>Maui：通过宿主注册的 IProgramUiHost 推送 ProgramUiPage。</summary>
        public static class UiHost
        {
            public static async Task<bool?> ShowMainPageAsync(CancellationToken token)
            {
                var host = FlyScreen.Services.Programs.ProgramUiHost.Current;
                if (host == null)
                {
                    Console.WriteLine("未注册 IProgramUiHost：请在 Maui 玄鸟内运行本程序。");
                    return null;
                }
                return await host.ShowAsync("ProgramUiPage.xaml", token).ConfigureAwait(false);
            }
        }
        """;

    // 完整 WPF 进程外 UiHost（从样板精简，保持反射启动 powershell）
    private const string EmbeddedWpfUiHost =
        """
        using System.IO;
        using System.Reflection;
        using System.Text;
        using System.Text.RegularExpressions;

        namespace FlyScreen.ProgramDevHost;

        /// <summary>
        /// 进程外加载 MainWindow.xaml（Maui-Win 进程内 WPF 会崩，故统一 OOP）。
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
                var cleaned = CleanLooseXaml(await File.ReadAllTextAsync(xamlPath, token).ConfigureAwait(false));
                await File.WriteAllTextAsync(xamlTemp, cleaned, Encoding.UTF8, token).ConfigureAwait(false);
                await File.WriteAllTextAsync(ps1, BuildPowerShell(xamlTemp), Encoding.UTF8, token).ConfigureAwait(false);
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
                    finally { TryDispose(proc); }
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
                return start.Invoke(null, [psi])
                    ?? throw new InvalidOperationException("无法启动 powershell 以显示 WPF 窗口。");
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
                catch { return Assembly.Load("System.Diagnostics.Process"); }
            }

            static async Task WaitForExitAsync(object proc, CancellationToken token)
            {
                var mi = proc.GetType().GetMethod("WaitForExitAsync", [typeof(CancellationToken)]);
                if (mi != null)
                {
                    await ((Task)mi.Invoke(proc, [token])!).ConfigureAwait(false);
                    return;
                }
                var hasExited = proc.GetType().GetProperty("HasExited")!;
                while (!(bool)hasExited.GetValue(proc)!)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
            }

            static async Task<string> ReadStdErrAsync(object proc, CancellationToken token)
            {
                var reader = proc.GetType().GetProperty("StandardError")!.GetValue(proc)!;
                var read = reader.GetType().GetMethod("ReadToEndAsync", [typeof(CancellationToken)])
                    ?? reader.GetType().GetMethod("ReadToEndAsync", Type.EmptyTypes);
                if (read == null) return "";
                var args = read.GetParameters().Length == 1 ? new object[] { token } : Array.Empty<object>();
                return await ((Task<string>)read.Invoke(reader, args)!).ConfigureAwait(false);
            }

            static int GetExitCode(object proc)
                => (int)proc.GetType().GetProperty("ExitCode")!.GetValue(proc)!;

            static void TryKill(object proc)
            {
                try
                {
                    if ((bool)proc.GetType().GetProperty("HasExited")!.GetValue(proc)!) return;
                    var kill = proc.GetType().GetMethod("Kill", [typeof(bool)])
                        ?? proc.GetType().GetMethod("Kill", Type.EmptyTypes);
                    kill?.Invoke(proc, kill.GetParameters().Length == 1 ? [true] : null);
                }
                catch { /* ignore */ }
            }

            static void TryDispose(object proc)
            {
                try { (proc as IDisposable)?.Dispose(); } catch { /* ignore */ }
            }

            static string CleanLooseXaml(string text)
            {
                var q = (char)34;
                text = Regex.Replace(text, @"\s+x:Class=" + q + "[^" + q + "]*" + q, "");
                text = Regex.Replace(text, @"\s+xmlns:local=" + q + "[^" + q + "]*" + q, "");
                text = Regex.Replace(text, @"\s+d:\w+=(" + q + "[^" + q + "]*" + q + "|'[^']*')", "");
                return text;
            }

            static string BuildPowerShell(string cleanedXamlPath)
            {
                var pathLit = "'" + cleanedXamlPath.Replace("'", "''") + "'";
                var sb = new StringBuilder();
                sb.AppendLine("Add-Type -AssemblyName PresentationFramework");
                sb.AppendLine("Add-Type -AssemblyName PresentationCore");
                sb.AppendLine("Add-Type -AssemblyName WindowsBase");
                sb.AppendLine("Add-Type -AssemblyName System.Xaml");
                sb.AppendLine("$path = " + pathLit);
                sb.AppendLine("$reader = [System.Xml.XmlReader]::Create($path)");
                sb.AppendLine("$win = [System.Windows.Markup.XamlReader]::Load($reader)");
                sb.AppendLine("if ($win -eq $null) { throw 'XamlReader.Load returned null' }");
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
                    if (File.Exists(bound)) return bound;
                }
                foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                {
                    var dir = new DirectoryInfo(start);
                    for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent!)
                    {
                        var path = Path.Combine(dir.FullName, fileName);
                        if (File.Exists(path)) return path;
                    }
                }
                throw new FileNotFoundException($"找不到 {fileName}（请放在程序目录下）。", fileName);
            }
        }
        """;
}
