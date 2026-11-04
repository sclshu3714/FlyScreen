using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using FlyScreen.Services;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.ScreenExtend;

/// <summary>
/// 扩展拓扑收拢监视：宿主程序集内嵌的 <c>FlyScreen.TopologyGuard.exe</c>
/// （显示名「玄鸟:守护」）解压到 AppData 后以独立进程名监视父进程。
/// 发布仍是单文件；不以 Content 塞进单文件包（嵌套 exe 会导致 apphost 0xc0000005）。
/// </summary>
public static class DisplayTopologyWatchdog
{
    public const string GuardExeName = "FlyScreen.TopologyGuard.exe";
    public const string GuardBinName = "FlyScreen.TopologyGuard.bin";
    public const string GuardResourceName = "FlyScreen.TopologyGuard.exe";

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static Process? _child;
    private static Timer? _keepAlive;
    private static bool _armed;

    public static string CancelEventName(int parentPid)
        => $@"Local\FlyScreen.DisplayWatchdog.Cancel.{parentPid}";

    public static void RunForParent(int parentPid) => RunWatchdogLoop(parentPid);

    public static void EnsureRunning()
    {
        lock (Gate)
        {
            _armed = true;
            TryStartGuardUnlocked();
            EnsureKeepAliveUnlocked();
        }
    }

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

            try { _child?.WaitForExit(800); } catch { /* ignore */ }
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

    private static void TryStartGuardUnlocked()
    {
        try
        {
            var pid = Environment.ProcessId;
            try
            {
                using var existing = EventWaitHandle.OpenExisting(CancelEventName(pid));
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // need start
            }

            var exe = EnsureGuardOnDisk();
            if (string.IsNullOrWhiteSpace(exe))
                return;

            _child?.Dispose();
            _child = null;
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
            // ignore
        }
    }

    /// <summary>
    /// 从入口/本程序集嵌入资源（或调试旁路文件）固化到 AppData\FlyScreen\guard\。
    /// </summary>
    private static string? EnsureGuardOnDisk()
    {
        try
        {
            var dir = Path.Combine(FlyScreenDataPaths.Root, "guard");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, GuardExeName);
            var stampPath = Path.Combine(dir, "guard.sha256");
            var tmp = dest + ".tmp";

            using var src = OpenGuardSource();
            if (src == null)
                return File.Exists(dest) ? dest : null;

            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }

            string hash;
            using (var sha = SHA256.Create())
            using (var fs = File.Create(tmp))
            using (var crypto = new CryptoStream(fs, sha, CryptoStreamMode.Write))
            {
                src.CopyTo(crypto);
                crypto.FlushFinalBlock();
                hash = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
            }

            if (File.Exists(dest) && File.Exists(stampPath))
            {
                try
                {
                    if (string.Equals(File.ReadAllText(stampPath).Trim(), hash, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tmp); } catch { /* ignore */ }
                        return dest;
                    }
                }
                catch { /* rewrite */ }
            }

            File.Copy(tmp, dest, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
            File.WriteAllText(stampPath, hash);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    private static Stream? OpenGuardSource()
    {
        foreach (var asm in EnumerateGuardAssemblies())
        {
            try
            {
                var direct = asm.GetManifestResourceStream(GuardResourceName);
                if (direct != null)
                    return direct;

                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (name.Equals(GuardResourceName, StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("." + GuardResourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        var s = asm.GetManifestResourceStream(name);
                        if (s != null)
                            return s;
                    }
                }
            }
            catch
            {
                // try next
            }
        }

        foreach (var name in new[] { GuardBinName, GuardExeName })
        {
            try
            {
                var beside = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(beside))
                    return File.OpenRead(beside);

                var procDir = Path.GetDirectoryName(Environment.ProcessPath);
                if (!string.IsNullOrEmpty(procDir))
                {
                    var alt = Path.Combine(procDir, name);
                    if (File.Exists(alt))
                        return File.OpenRead(alt);
                }
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private static IEnumerable<Assembly> EnumerateGuardAssemblies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in new[] { Assembly.GetEntryAssembly(), typeof(DisplayTopologyWatchdog).Assembly })
        {
            if (asm == null) continue;
            var key = asm.FullName ?? asm.GetHashCode().ToString();
            if (!seen.Add(key)) continue;
            yield return asm;
        }
    }

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
