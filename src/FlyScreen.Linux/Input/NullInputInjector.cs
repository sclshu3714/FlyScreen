using FlyScreen.Services.Network;

namespace FlyScreen.Linux.Input;

/// <summary>无头 / 无注入能力：忽略键鼠消息，保持连接。</summary>
public sealed class NullInputInjector : ILinuxInputInjector
{
    public string BackendName => "none";
    public bool IsAvailable => false;

    public void MoveAbsoluteNormalized(double nx, double ny) { }
    public void MoveRelative(int dx, int dy) { }
    public void MouseButton(MouseButtonPayload p) { }
    public void MouseWheel(MouseWheelPayload p) { }
    public void Key(KeyEventPayload p) { }
}
