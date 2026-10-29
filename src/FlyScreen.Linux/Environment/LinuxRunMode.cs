namespace FlyScreen.Linux.Environment;

/// <summary>进程运行形态：与会话类型正交（有桌面也可强制 CLI）。</summary>
public enum LinuxRunMode
{
    /// <summary>有图形会话 → GUI，否则 → CLI。</summary>
    Auto,
    Gui,
    Cli,
    /// <summary>非交互后台（systemd / nohup），复用 CLI 宿主逻辑。</summary>
    Daemon
}
