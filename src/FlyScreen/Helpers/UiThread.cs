using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace FlyScreen.Helpers;

internal static class UiThread
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            action();
            return;
        }

        // 始终异步投递，避免在 Measure/绑定回调里同步触发 PropertyChanged 导致栈溢出
        dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    public static void Send(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <summary>
    /// 等待任务完成；若在 UI 线程上则泵送消息，避免与 Dispatcher.Invoke 死锁
    /// （例如剪贴板 GetData 等待接收，而接收又要回 UI 更新列表）。
    /// </summary>
    public static void WaitWithPumping(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || !dispatcher.CheckAccess())
        {
            try { task.Wait(timeout); } catch { /* ignore */ }
            return;
        }

        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();
        EventHandler? tick = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };

        void CompleteFrame()
        {
            try { timer.Stop(); } catch { /* ignore */ }
            if (tick != null) timer.Tick -= tick;
            frame.Continue = false;
        }

        tick = (_, _) =>
        {
            if (task.IsCompleted || sw.Elapsed >= timeout)
                CompleteFrame();
        };
        timer.Tick += tick;
        timer.Start();

        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke((Action)CompleteFrame, DispatcherPriority.Send),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Dispatcher.PushFrame(frame);
    }

    public static T? WaitWithPumping<T>(Task<T> task, TimeSpan timeout)
    {
        WaitWithPumping((Task)task, timeout);
        try
        {
            return task.IsCompletedSuccessfully ? task.Result : default;
        }
        catch
        {
            return default;
        }
    }
}
