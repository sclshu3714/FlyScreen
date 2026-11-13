namespace FlyScreen.Relay.Services;

/// <summary>
/// 管理页 SSE 唤醒：注册/离线/会话开闭等 Notify（短防抖合并）。
/// 流量计数靠 SSE 1s 兜底。勿在候选地址热路径上狂唤。
/// </summary>
public sealed class AdminChangeNotifier
{
    private readonly object _gate = new();
    private TaskCompletionSource _tcs = NewTcs();
    private long _version;
    private int _debounceBusy;

    public long Version => Interlocked.Read(ref _version);

    public void Notify()
    {
        Interlocked.Increment(ref _version);
        if (Interlocked.CompareExchange(ref _debounceBusy, 1, 0) != 0)
            return;
        _ = FireDebouncedAsync();
    }

    private async Task FireDebouncedAsync()
    {
        try
        {
            await Task.Delay(120).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _debounceBusy, 0);
        }

        TaskCompletionSource prev;
        lock (_gate)
        {
            prev = _tcs;
            _tcs = NewTcs();
        }
        prev.TrySetResult();
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task wait;
        lock (_gate) wait = _tcs.Task;
        try
        {
            await wait.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 周期性兜底
        }
    }

    private static TaskCompletionSource NewTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
