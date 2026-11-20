using FlyScreen.Services.Programs;
using Xunit;

namespace FlyScreen.Core.Tests;

public sealed class ProgramScriptHostUsingHoistTests
{
    [Fact]
    public void WrapAsEntry_PlacesUsingOutsideMethod()
    {
        var source =
            """
            using com.xuanniao.duanshang;

            Module1.AddSex();
            """;

        var (code, lineOffset) = ProgramScriptHost.WrapAsEntry(source);

        Assert.Equal(0, lineOffset);
        Assert.Contains("using com.xuanniao.duanshang;", code);
        var usingIdx = code.IndexOf("using com.xuanniao.duanshang;", StringComparison.Ordinal);
        var methodIdx = code.IndexOf("public static async Task RunAsync", StringComparison.Ordinal);
        Assert.True(usingIdx > 0 && usingIdx < methodIdx);
        Assert.Contains("#line 1", code);
        // using 后空行通常进入方法体，#line 指向该空行（用户第 2 行），Module1 仍映射为第 3 行
        Assert.True(
            code.Contains("#line 2") || code.Contains("#line 3"),
            "expected body #line near user line 2/3");
    }

    [Fact]
    public void Compile_WithUsingAndExtraModule_Succeeds()
    {
        var entry =
            """
            using com.xuanniao.duanshang;

            Module1.AddSex();
            """;

        var module =
            """
            namespace com.xuanniao.duanshang;

            public static class Module1
            {
                public static void AddSex() { }
            }
            """;

        var result = ProgramScriptHost.Compile(
            entry,
            emitAssembly: false,
            extras: [("Module1.cs", module)],
            extraReferences: null,
            entryPath: "main.cs");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString())));
        Assert.Equal(0, result.LineOffset);
    }

    [Fact]
    public void Compile_BodyError_MapsToUserLine()
    {
        var entry =
            """
            using System;

            UndefinedSymbol();
            """;

        var result = ProgramScriptHost.Compile(entry, emitAssembly: false);
        Assert.False(result.Success);

        var mapped = ProgramScriptHost.MapDiagnostics(result.Diagnostics, "main.cs", result.LineOffset);
        var err = Assert.Single(mapped, d => d.Severity == "error");
        Assert.Equal(3, err.Line);
    }
}
