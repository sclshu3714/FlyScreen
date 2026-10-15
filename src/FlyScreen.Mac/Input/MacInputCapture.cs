using FlyScreen.Mac.Permissions;

namespace FlyScreen.Mac.Input;

/// <summary>
/// 本机作为主机时捕获键鼠（CGEventTap）。W1 第 3–5 周实现；当前为可启停占位。
/// </summary>
public sealed class MacInputCapture : IDisposable
{
    public bool IsRunning { get; private set; }
    public bool CanStart => AccessibilityPermission.IsMacOS && AccessibilityPermission.IsTrusted();

    public event Action<string>? StatusChanged;

    public bool Start()
    {
        if (!CanStart)
        {
            StatusChanged?.Invoke("需要辅助功能权限才能捕获键鼠。请在系统设置中授权后重试。");
            return false;
        }

        // TODO: CGEventTapCreate + CFRunLoopSource（边缘切换主机路径）
        // 导航/粘滞出界须复用 Core：FlyScreen.Services.Input.EdgeMouseVirtualCursor
        // （与 Win InputRouter 一致：进入边粘滞 + CursorOwner=当前对端时勿放弃控制）
        IsRunning = true;
        StatusChanged?.Invoke("键鼠捕获占位已启动（完整 EdgeMouse 捕获后续迭代）。");
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        StatusChanged?.Invoke("键鼠捕获已停止。");
    }

    public void Dispose() => Stop();
}
