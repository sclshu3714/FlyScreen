using FlyScreen.Services.Programs;
using Xunit;

namespace FlyScreen.Core.Tests;

public sealed class ProgramExternalDevScaffoldTests
{
    [Fact]
    public void Ensure_CSharp_WritesDevHostAndCsproj()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlyScreenTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ProgramExternalDevScaffold.Ensure(dir, ProgramLanguage.CSharp, packages: null);

            Assert.True(File.Exists(Path.Combine(dir, "DevHost.cs")));
            Assert.True(File.Exists(Path.Combine(dir, "program.csproj")));
            Assert.True(File.Exists(Path.Combine(dir, "Directory.Build.props")));
            Assert.True(File.Exists(Path.Combine(dir, "README-DEV.md")));
            Assert.True(File.Exists(Path.Combine(dir, ".vscode", "extensions.json")));
            var csproj = File.ReadAllText(Path.Combine(dir, "program.csproj"));
            Assert.Contains("DevHost.cs", csproj, StringComparison.Ordinal);
            Assert.Contains("FlyScreen.Core.dll", csproj, StringComparison.Ordinal);
            Assert.DoesNotContain("ProjectReference", csproj, StringComparison.Ordinal);
            Assert.Contains("ProgramExternalDevHost", File.ReadAllText(Path.Combine(dir, "DevHost.cs")), StringComparison.Ordinal);
            var props = File.ReadAllText(Path.Combine(dir, "Directory.Build.props"));
            Assert.DoesNotContain(".csproj", props, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Ensure_PowerShell_WritesReadmeWithoutDevHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlyScreenTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ProgramExternalDevScaffold.Ensure(dir, ProgramLanguage.PowerShell);

            Assert.True(File.Exists(Path.Combine(dir, "README-DEV.md")));
            Assert.False(File.Exists(Path.Combine(dir, "DevHost.cs")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
