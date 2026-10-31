using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;

namespace FlyScreen.Linux.Input;

/// <summary>X11 上跟住正在拖的指针，直到左键松开。</summary>
public sealed class X11EdgeDragPointer : IEdgeDragPointer
{
    private readonly X11InputInjector _x11;

    public X11EdgeDragPointer(X11InputInjector x11) => _x11 = x11;

    public bool TryRead(out int x, out int y, out bool leftDown)
    {
        var ok = _x11.TryReadPointer(out x, out y, out leftDown, out _, out _);
        return ok;
    }

    public void WarpToCenter() => _x11.WarpToCenter();

    public void ScreenSize(out int width, out int height)
    {
        if (!_x11.TryReadPointer(out _, out _, out _, out width, out height))
        {
            width = 1920;
            height = 1080;
        }
    }

    public void DismissLocalDrag()
    {
        _x11.Key(new KeyEventPayload { VirtualKey = 0x1B, Down = true });
        _x11.Key(new KeyEventPayload { VirtualKey = 0x1B, Down = false });
    }
}
