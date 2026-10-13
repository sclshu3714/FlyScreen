using System.Threading;

namespace FlyScreen.Helpers;

/// <summary>
/// 单实例：已有进程则发信号激活其窗口；否则持有互斥量并监听激活请求。
/// </summary>
public sealed class SingleInstanceHelper : IDisposable
{
    private const string MutexName = @"Local\FlyScreen.SingleInstance.Mutex";
    private const string EventName = @"Local\FlyScreen.SingleInstance.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private CancellationTokenSource? _listenCts;
    private Thread? _listenThread;
    private bool _owned;

    private SingleInstanceHelper(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _owned = true;
    }

    /// <summary>
    /// 尝试成为主实例。失败则已向已有实例发送「显示窗口」信号。
    /// </summary>
    public static bool TryStartPrimary(out SingleInstanceHelper? instance)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
        }
        catch
        {
            instance = null;
            return false;
        }

        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一实例异常退出，接管互斥量
            acquired = true;
        }

        EventWaitHandle showEvent;
        try
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch
        {
            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { /* ignore */ }
            }
            mutex.Dispose();
            instance = null;
            return false;
        }

        if (!acquired)
        {
            try { showEvent.Set(); } catch { /* ignore */ }
            showEvent.Dispose();
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstanceHelper(mutex, showEvent);
        return true;
    }

    /// <summary>在后台监听「显示窗口」信号，回调应切回 UI 线程。</summary>
    public void StartListening(Action onShowRequested)
    {
        if (!_owned) return;
        _listenCts = new CancellationTokenSource();
        var ct = _listenCts.Token;
        _listenThread = new Thread(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (_showEvent.WaitOne(500))
                    {
                        try { onShowRequested(); }
                        catch { /* ignore */ }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // shutting down
            }
        })
        {
            IsBackground = true,
            Name = "FlyScreen.SingleInstance"
        };
        _listenThread.Start();
    }

    public void Dispose()
    {
        _listenCts?.Cancel();
        try { _listenThread?.Join(1000); } catch { /* ignore */ }
        _listenCts?.Dispose();
        _listenCts = null;
        _listenThread = null;

        try { _showEvent.Dispose(); } catch { /* ignore */ }
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
            _owned = false;
        }
        try { _mutex.Dispose(); } catch { /* ignore */ }
    }
}
