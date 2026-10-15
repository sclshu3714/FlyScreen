using FlyScreen.Services.Network;

namespace FlyScreen.Linux.Input;

public interface ILinuxInputInjector
{
    string BackendName { get; }
    bool IsAvailable { get; }
    void MoveAbsoluteNormalized(double nx, double ny);
    void MoveRelative(int dx, int dy);
    void MouseButton(MouseButtonPayload p);
    void MouseWheel(MouseWheelPayload p);
    void Key(KeyEventPayload p);

    /// <summary>被远程控制时确保系统光标可见（X11 实现；其余后端可空操作）。</summary>
    void EnsureCursorVisible() { }

    /// <summary>当前指针归一化坐标（软件光标用）。</summary>
    bool TryGetPointerNormalized(out double nx, out double ny)
    {
        nx = ny = 0;
        return false;
    }
}
