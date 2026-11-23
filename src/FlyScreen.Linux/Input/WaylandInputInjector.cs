using FlyScreen.Services.Network;

namespace FlyScreen.Linux.Input;

/// <summary>
/// 纯 Wayland、且无 XWayland（无可用 DISPLAY/X11）时的占位注入。
/// 正常 Wayland 桌面由 <see cref="LinuxInputFactory"/> 优先挂 X11/XWayland，本类仅作回退。
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
