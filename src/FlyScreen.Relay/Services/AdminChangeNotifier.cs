namespace FlyScreen.Relay.Services;

/// <summary>
/// 管理页 SSE 唤醒信号：设备注册/离线、会话开闭等结构变化时立即 Notify，
/// 流量类指标仍靠 SSE 侧 1s 兜底刷新。
/// </summary>
public sealed class AdminChangeNotifier
{
    private readonly object _gate = new();
    private TaskCompletionSource _tcs = NewTcs();
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public void Notify()
    {
        Interlocked.Increment(ref _version);
        TaskCompletionSource prev;
        lock (_gate)
        {
            prev = _tcs;
            _tcs = NewTcs();
        }
        prev.TrySetResult();
    }

    /// <summary>等待下一次 Notify，或直到 timeout / 取消。</summary>
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
            // 周期性兜底刷新
        }
    }

    private static TaskCompletionSource NewTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
