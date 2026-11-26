using System.Diagnostics;
using FlyScreen.Linux.Environment;

namespace FlyScreen.Linux.LockInput;

/// <summary>通过 loginctl LockedHint 轮询本机是否锁屏。</summary>
public static class LinuxLockScreenSession
{
    private static int _started;
    private static volatile bool _locked;
    private static Timer? _timer;

    public static bool IsLocked => _locked;

    public static event Action<bool>? Changed;

    public static void EnsureStarted()
    {
        if (!LinuxRuntimeEnvironment.IsLinux) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public static void Stop()
    {
        try { _timer?.Dispose(); } catch { /* ignore */ }
        _timer = null;
        Interlocked.Exchange(ref _started, 0);
    }

    private static void Poll()
    {
        var locked = ReadLockedHint();
        if (_locked == locked) return;
        _locked = locked;
        try { Changed?.Invoke(locked); } catch { /* ignore */ }
    }

    private static bool ReadLockedHint()
    {
        try
        {
            var session = System.Environment.GetEnvironmentVariable("XDG_SESSION_ID");
            if (string.IsNullOrWhiteSpace(session))
                session = TryReadSelfSessionId();
            if (string.IsNullOrWhiteSpace(session))
                return false;

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "loginctl",
                Arguments = $"show-session {session} -p LockedHint --value",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return false;
            var output = (p.StandardOutput.ReadToEnd() ?? "").Trim();
            p.WaitForExit(1500);
            return output.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || output.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || output == "1";
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadSelfSessionId()
    {
        try
        {
            var path = "/proc/self/sessionid";
            if (!File.Exists(path)) return null;
            var id = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(id) || id == "4294967295" ? null : id;
        }
        catch
        {
            return null;
        }
    }
}
