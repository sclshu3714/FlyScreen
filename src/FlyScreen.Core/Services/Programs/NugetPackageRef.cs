namespace FlyScreen.Services.Programs;

/// <summary>C# 用户程序声明的 NuGet 依赖（写入 program.csproj / program.json）。</summary>
public sealed class NugetPackageRef
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
}
