using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlyScreen.LockInputHelper;

/// <summary>
/// 服务在 Session 0；键鼠注入须在交互会话的 SYSTEM 进程中打开 Winlogon 桌面。
/// 把同 exe 以 --session-agent 拉进活动控制台会话（SYSTEM 令牌 + TokenSessionId）。
/// </summary>
internal static class SessionAgent
{
    private static Process? _agent;
    private static readonly object Gate = new();

    public static void EnsureRunning()
    {
        lock (Gate)
        {
            if (_agent is { HasExited: false })
                return;

            try { _agent?.Dispose(); } catch { /* ignore */ }
            _agent = null;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return;

            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
                return;

            if (!TryLaunchSystemInSession(exe, "--session-agent", sessionId, out var proc) || proc == null)
                return;

            _agent = proc;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try
            {
                if (_agent is { HasExited: false })
                    _agent.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            try { _agent?.Dispose(); } catch { /* ignore */ }
            _agent = null;
        }
    }

    private static bool TryLaunchSystemInSession(string exe, string args, uint sessionId, out Process? process)
    {
        process = null;
        IntPtr selfToken = IntPtr.Zero;
        IntPtr primary = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(
                    GetCurrentProcess(),
                    TokenDuplicate | TokenAssignPrimary | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId,
                    out selfToken))
                return false;

            if (!DuplicateTokenEx(selfToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                return false;

            if (!SetTokenInformation(primary, TokenSessionIdClass, ref sessionId, sizeof(uint)))
                return false;

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default"
            };
            var cmd = $"\"{exe}\" {args}";
            if (!CreateProcessAsUser(
                    primary,
                    null,
                    cmd,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateUnicodeEnvironment | CreateNewProcessGroup | CreateNoWindow,
                    IntPtr.Zero,
                    Path.GetDirectoryName(exe),
                    ref si,
                    out var pi))
                return false;

            CloseHandle(pi.hThread);
            try
            {
                process = Process.GetProcessById(pi.dwProcessId);
            }
            catch
            {
                CloseHandle(pi.hProcess);
                return false;
            }

            CloseHandle(pi.hProcess);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (selfToken != IntPtr.Zero) CloseHandle(selfToken);
            if (primary != IntPtr.Zero) CloseHandle(primary);
        }
    }

    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint TokenAllAccess = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionIdClass = 12;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateNoWindow = 0x08000000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        ref uint tokenInformation,
        int tokenInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }
}
