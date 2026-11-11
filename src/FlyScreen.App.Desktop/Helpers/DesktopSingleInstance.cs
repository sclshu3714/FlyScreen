using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FlyScreen.Helpers;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>Avalonia 桌面（Mac / Linux）单实例：入口抢互斥，主窗创建后听激活信号。</summary>
public static class DesktopSingleInstance
{
    public static SingleInstanceHelper? Current { get; private set; }

    /// <summary>成为主实例；失败时已向已有进程发激活信号。</summary>
    public static bool TryEnter()
    {
        if (!SingleInstanceHelper.TryStartPrimary(out var instance))
            return false;
        Current = instance;
        return true;
    }

    public static void Attach(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var si = Current;
        if (si == null) return;
        si.StartListening(() =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (desktop.MainWindow is MainWindow mw)
                    mw.BringToFront();
                else if (desktop.MainWindow is { } w)
                {
                    w.Show();
                    w.WindowState = Avalonia.Controls.WindowState.Normal;
                    w.Activate();
                }
            });
        });
    }

    public static void Release()
    {
        try { Current?.Dispose(); }
        catch { /* ignore */ }
        Current = null;
    }
}
