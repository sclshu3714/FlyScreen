using FlyScreen.Services.Programs;
using Xunit;

namespace FlyScreen.Core.Tests;

public sealed class ProgramScriptHostReferenceTests
{
    [Fact]
    public void HasManagedMetadata_True_ForCoreLib()
    {
        var path = typeof(object).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(ProgramScriptHost.HasManagedMetadata(path));
        Assert.True(ProgramScriptHost.TryCreateReferenceFromFile(
            path, out var reference, out var error, out var nonManaged));
        Assert.NotNull(reference);
        Assert.Null(error);
        Assert.False(nonManaged);
    }

    [Fact]
    public void HasManagedMetadata_False_ForGarbageBytes()
    {
        Assert.False(ProgramScriptHost.HasManagedMetadata([0x4D, 0x5A, 0, 0, 1, 2, 3, 4]));
        Assert.False(ProgramScriptHost.HasManagedMetadata([]));
    }

    [Fact]
    public void TryCreateReferenceFromFile_SkipsNativeSystemDll()
    {
        var kernel32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "kernel32.dll");
        if (!File.Exists(kernel32))
            return; // 非 Windows CI 跳过

        Assert.False(ProgramScriptHost.HasManagedMetadata(kernel32));
        Assert.False(ProgramScriptHost.TryCreateReferenceFromFile(
            kernel32, out var reference, out var error, out var nonManaged));
        Assert.Null(reference);
        Assert.True(nonManaged);
        Assert.Contains("不是托管程序集", error);
    }
}
