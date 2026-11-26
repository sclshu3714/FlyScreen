using System.ServiceProcess;
using FlyScreen.LockInput;

namespace FlyScreen.LockInputHelper;

/// <summary>Session 0 服务：保活交互会话中的注入代理（真正的管道在 --session-agent）。</summary>
internal sealed class LockInputService : ServiceBase
{
    private CancellationTokenSource? _cts;
    private Thread? _watchThread;

    public LockInputService()
    {
        ServiceName = LockScreenInputProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnStart(string[] args)
        => StartWork();

    protected override void OnStop()
        => StopWork();

    protected override void OnShutdown()
        => StopWork();

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);
        // 锁屏/解锁/登录后重新确保代理在活动会话
        try { SessionAgent.EnsureRunning(); } catch { /* ignore */ }
    }

    public void StartWork()
    {
        _cts = new CancellationTokenSource();
        try { SessionAgent.EnsureRunning(); } catch { /* ignore */ }

        _watchThread = new Thread(() => WatchLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "FlyScreen.LockInput.Watch"
        };
        _watchThread.Start();
    }

    public void StopWork()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _watchThread?.Join(2000); } catch { /* ignore */ }
        SessionAgent.Stop();
        _cts?.Dispose();
        _cts = null;
        _watchThread = null;
    }

    private static void WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { SessionAgent.EnsureRunning(); } catch { /* ignore */ }
            try { ct.WaitHandle.WaitOne(5000); }
            catch { break; }
        }
    }
}
