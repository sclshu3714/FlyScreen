using System.Diagnostics;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.ScreenExtend;

/// <summary>
/// 扩展拓扑收拢监视：由独立命名的 <c>FlyScreen.TopologyGuard.exe</c> 监视父进程；
/// 主进程被杀时仍执行 <c>DisplaySwitch /internal</c>，避免与主应用同组被任务管理器一并结束。
/// Guard 若被先杀，脏标记期间会定时重拉。正常 Finalize 时 <see cref="Disarm"/> 解除监视。
/// </summary>
public static class DisplayTopologyWatchdog
{
    public const string GuardExeName = "FlyScreen.TopologyGuard.exe";

    /// <summary>Guard 被杀后，主进程侧多久重拉一次。</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static Process? _child;
    private static Timer? _keepAlive;
    private static bool _armed;

    public static string CancelEventName(int parentPid)
        => $@"Local\FlyScreen.DisplayWatchdog.Cancel.{parentPid}";

    /// <summary>TopologyGuard 入口：阻塞直到父进程退出或被 Disarm，再按需收拢拓扑。</summary>
    public static void RunForParent(int parentPid) => RunWatchdogLoop(parentPid);

    /// <summary>扩展投影开始后调用：确保监视子进程在跑，并开启保活重拉。</summary>
    public static void EnsureRunning()
    {
        lock (Gate)
        {
            _armed = true;
            TryStartGuardUnlocked();
            EnsureKeepAliveUnlocked();
        }
    }

    /// <summary>正常 Finalize 完成后调用：停止保活，让监视进程退出且不再 /internal。</summary>
    public static void Disarm()
    {
        lock (Gate)
        {
            _armed = false;
            StopKeepAliveUnlocked();

            try
            {
                using var ev = EventWaitHandle.OpenExisting(CancelEventName(Environment.ProcessId));
                ev.Set();
            }
            catch
            {
                // 无监视进程
            }

            try
            {
                _child?.WaitForExit(800);
            }
            catch { /* ignore */ }

            try { _child?.Dispose(); } catch { /* ignore */ }
            _child = null;
        }
    }

    private static void EnsureKeepAliveUnlocked()
    {
        if (_keepAlive != null) return;
        _keepAlive = new Timer(
            static _ => KeepAliveTick(),
            null,
            KeepAliveInterval,
            KeepAliveInterval);
    }

    private static void StopKeepAliveUnlocked()
    {
        try { _keepAlive?.Dispose(); } catch { /* ignore */ }
        _keepAlive = null;
    }

    private static void KeepAliveTick()
    {
        lock (Gate)
        {
            if (!_armed || !DisplayTopologyDirty.IsMarked)
            {
                _armed = false;
                StopKeepAliveUnlocked();
                return;
            }

            TryStartGuardUnlocked();
        }
    }

    /// <summary>若 cancel 事件已存在（Guard 仍活着）则跳过；否则拉起。</summary>
    private static void TryStartGuardUnlocked()
    {
        try
        {
            var pid = Environment.ProcessId;
            try
            {
                using var existing = EventWaitHandle.OpenExisting(CancelEventName(pid));
                return; // 已在监视
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // need start / restart
            }

            var exe = ResolveGuardExe();
            if (string.IsNullOrWhiteSpace(exe))
                return;

            _child?.Dispose();
            _child = null;

            // 经 cmd start 拉起，脱离本进程树；独立 exe 名避免任务管理器应用组一并杀掉。
            StartDetachedGuard(exe, pid.ToString());

            for (var i = 0; i < 40; i++)
            {
                try
                {
                    using var ready = EventWaitHandle.OpenExisting(CancelEventName(pid));
                    return;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch
        {
            // ignore — 仍有脏标记 + 下次启动恢复
        }
    }

    private static string? ResolveGuardExe()
    {
        static string? Beside(string? basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath)) return null;
            var dir = Path.GetDirectoryName(basePath);
            if (string.IsNullOrWhiteSpace(dir)) return null;
            var path = Path.Combine(dir, GuardExeName);
            return File.Exists(path) ? path : null;
        }

        var fromBase = Path.Combine(AppContext.BaseDirectory, GuardExeName);
        return Beside(Environment.ProcessPath)
               ?? Beside(Process.GetCurrentProcess().MainModule?.FileName)
               ?? (File.Exists(fromBase) ? fromBase : null);
    }

    /// <summary>
    /// 用 cmd start 脱离父子树；失败则退回直接 Process.Start（仍有独立进程名防组杀）。
    /// </summary>
    private static void StartDetachedGuard(string exe, string parentPidArg)
    {
        try
        {
            var quotedExe = exe.Contains(' ') ? $"\"{exe}\"" : exe;
            using var launcher = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"FlyScreen.TopologyGuard\" /B {quotedExe} {parentPidArg}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            launcher?.WaitForExit(2000);
            return;
        }
        catch
        {
            // fall through
        }

        try
        {
            _child = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = parentPidArg,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
            // ignore
        }
    }

    private static void RunWatchdogLoop(int parentPid)
    {
        using var cancel = CreateCancelEvent(parentPid);

        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(parentPid);
        }
        catch
        {
            CollapseTopologyBestEffort();
            return;
        }

        try
        {
            var watcher = new Thread(() =>
            {
                try { parent.WaitForExit(); }
                catch { /* ignore */ }
                try { cancel?.Set(); }
                catch { /* ignore */ }
            })
            {
                IsBackground = true,
                Name = "FlyScreen.DisplayWatchdog.Parent"
            };
            watcher.Start();

            if (cancel != null)
                cancel.WaitOne();
            else
                parent.WaitForExit();

            var parentDead = true;
            try { parentDead = parent.HasExited; }
            catch { parentDead = true; }

            if (parentDead)
                CollapseTopologyBestEffort();
        }
        finally
        {
            try { parent.Dispose(); } catch { /* ignore */ }
        }
    }

    private static EventWaitHandle? CreateCancelEvent(int parentPid)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.ManualReset, CancelEventName(parentPid));
        }
        catch
        {
            return null;
        }
    }

    private static void CollapseTopologyBestEffort()
    {
        try
        {
            DisplayProjection.TryApply(DisplayProjectionMode.Internal);
        }
        catch { /* ignore */ }

        try
        {
            if (MttVddPipeClient.IsPipePresentAsync().GetAwaiter().GetResult())
                _ = MttVddPipeClient.SetDisplayCountAsync(0).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }

        try { DisplayTopologyDirty.Clear(); } catch { /* ignore */ }
    }
}
