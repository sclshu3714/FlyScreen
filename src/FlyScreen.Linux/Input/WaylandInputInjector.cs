using FlyScreen.Services.Network;

namespace FlyScreen.Linux.Input;

/// <summary>
/// Wayland 注入占位：全局注入受合成器限制，W2 不承诺 EdgeMouse。
/// 后续可接 ydotool / 门户协议；当前保持连接并记录不可用。
/// </summary>
public sealed class WaylandInputInjector : ILinuxInputInjector
{
    public string BackendName => "wayland";
    public bool IsAvailable => false;

    public void MoveAbsoluteNormalized(double nx, double ny) { }
    public void MoveRelative(int dx, int dy) { }
    public void MouseButton(MouseButtonPayload p) { }
    public void MouseWheel(MouseWheelPayload p) { }
    public void Key(KeyEventPayload p) { }
}
