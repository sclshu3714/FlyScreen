namespace FlyScreen.ScreenExtend;

/// <summary>
/// 全局唯一的 MTT 环境修复会话：弹窗关闭不中断；重开可附着进度；禁止并发重复执行。
/// </summary>
public static class MttVddRepairSession
{
    private static readonly object Gate = new();
    private static Task<(bool Ok, string Message)>? _running;
    private static string _status = "";
    private static readonly List<WeakReference<IProgress<string>>> Listeners = [];

    /// <summary>是否正在修复（含安装中）。</summary>
    public static bool IsRunning
    {
        get
        {
            lock (Gate)
                return _running is { IsCompleted: false };
        }
    }

    /// <summary>最近一次进度文案。</summary>
    public static string Status
    {
        get
        {
            lock (Gate) return _status;
        }
    }

    /// <summary>运行状态变化（开始/结束）或进度更新时触发。</summary>
    public static event Action? Changed;

    public static void AttachProgress(IProgress<string> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (Gate)
        {
            PruneListeners_NoLock();
            if (Listeners.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, progress)))
                return;
            Listeners.Add(new WeakReference<IProgress<string>>(progress));
            if (!string.IsNullOrEmpty(_status))
                progress.Report(_status);
        }
    }

    public static void DetachProgress(IProgress<string> progress)
    {
        lock (Gate)
        {
            Listeners.RemoveAll(w =>
                !w.TryGetTarget(out var t) || ReferenceEquals(t, progress));
        }
    }

    /// <summary>
    /// 开始或附着到已有修复任务。关闭弹窗不会取消；再次调用返回同一 Task。
    /// </summary>
    public static Task<(bool Ok, string Message)> RunOrAttachAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (progress != null)
            AttachProgress(progress);

        lock (Gate)
        {
            if (_running is { IsCompleted: false })
                return _running;

            _status = "正在修复…";
            _running = RunCoreAsync(ct);
            RaiseChanged();
            return _running;
        }
    }

    private static async Task<(bool Ok, string Message)> RunCoreAsync(CancellationToken ct)
    {
        try
        {
            var bridge = new Progress<string>(Report);
            var result = await MttVddInstaller.TryRepairAsync(bridge, ct).ConfigureAwait(false);
            Report(result.Message);
            return result;
        }
        catch (OperationCanceledException)
        {
            Report("修复已取消");
            return (false, "修复已取消");
        }
        catch (Exception ex)
        {
            var msg = "修复失败：" + ex.Message;
            Report(msg);
            return (false, msg);
        }
        finally
        {
            lock (Gate)
            {
                // 仅清当前任务引用；状态文案保留供 UI 展示
                _running = null;
            }

            RaiseChanged();
        }
    }

    private static void Report(string status)
    {
        List<IProgress<string>> snapshot;
        lock (Gate)
        {
            _status = status ?? "";
            PruneListeners_NoLock();
            snapshot = Listeners
                .Select(w => w.TryGetTarget(out var t) ? t : null)
                .Where(t => t != null)
                .Cast<IProgress<string>>()
                .ToList();
        }

        var text = status ?? "";
        foreach (var p in snapshot)
        {
            try { p.Report(text); }
            catch { /* ignore */ }
        }

        RaiseChanged();
    }

    private static void PruneListeners_NoLock()
        => Listeners.RemoveAll(w => !w.TryGetTarget(out _));

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* ignore */ }
    }
}
