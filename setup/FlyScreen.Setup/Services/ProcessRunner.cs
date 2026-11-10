using System.Diagnostics;
using System.Text;

namespace FlyScreen.Setup.Services;

public sealed class ProcessRunner
{
    public async Task<int> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        Action<string> onOutput,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) onOutput(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) onOutput(e.Data);
        };
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        onOutput($"> {fileName} {arguments}");
        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程: {fileName}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
            tcs.TrySetCanceled(ct);
        });

        return await tcs.Task.ConfigureAwait(false);
    }
}
