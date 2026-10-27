using FlyScreen.Services.Network;

namespace FlyScreen.Mac.Input;

/// <summary>将协议键鼠消息注入本机（被控）。</summary>
public interface IMacInputInjector
{
    bool IsAvailable { get; }
    void MoveAbsoluteNormalized(double nx, double ny);
    void MoveRelative(int dx, int dy);
    void MouseButton(MouseButtonPayload p);
    void MouseWheel(MouseWheelPayload p);
    void Key(KeyEventPayload p);
}
