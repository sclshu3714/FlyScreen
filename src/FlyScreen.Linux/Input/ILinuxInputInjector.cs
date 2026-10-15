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
}
