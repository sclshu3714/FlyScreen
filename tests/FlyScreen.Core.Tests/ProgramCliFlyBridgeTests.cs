using FlyScreen.Services.Programs;
using Xunit;

namespace FlyScreen.Core.Tests;

public sealed class ProgramCliFlyBridgeTests
{
    [Fact]
    public void RewriteFlyMembers_ConvertsCallsAndProperties()
    {
        var src =
            """
            $apk = Fly.GetString("browseFile")
            if (Fly.GetBool("enable")) { Write-Host Fly.Ready }
            """;

        var rewritten = ProgramCliFlyBridge.RewriteFlyMembers(src);

        Assert.Contains("[Fly]::GetString(\"browseFile\")", rewritten);
        Assert.Contains("[Fly]::GetBool(\"enable\")", rewritten);
        Assert.Contains("[Fly]::Ready", rewritten);
        Assert.DoesNotContain("Fly.GetString", rewritten);
    }

    [Fact]
    public void Compile_NonCSharp_SkipsWithSuccess()
    {
        var result = ProgramCompiler.Compile(ProgramLanguage.PowerShell, "Write-Host hi", "main.ps1");
        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Id == "FLY0010" && d.Severity == "info");
    }
}
