using System.ServiceProcess;

namespace FlyScreen.LockInputHelper;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)))
        {
            ServiceBase.Run(new LockInputService());
            return 0;
        }

        if (args.Any(a => string.Equals(a, "--session-agent", StringComparison.OrdinalIgnoreCase)))
            return RunSessionAgent();

        // 控制台调试：直接跑管道（当前会话）
        Console.WriteLine("FlyScreen LockInputHelper console mode. Ctrl+C to exit.");
        return RunSessionAgent();
    }

    private static int RunSessionAgent()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var thread = new Thread(() => PipeServer.Run(cts.Token))
        {
            IsBackground = true,
            Name = "FlyScreen.LockInput.Pipe"
        };
        thread.Start();
        try { cts.Token.WaitHandle.WaitOne(); }
        catch { /* ignore */ }
        cts.Cancel();
        try { thread.Join(2000); } catch { /* ignore */ }
        return 0;
    }
}
