using FlyScreen.Services.Programs;
using System.IO;

// 外部开发入口：dotnet run / VS F5。
// 玄鸟内运行仍走进程内 Roslyn，不会编译本文件（勿加入 program.json ExtraFiles）。
internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args) {
        var folder = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? Path.GetFullPath(args[0])
            : FindProgramFolder();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => {
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