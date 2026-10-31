#if ANDROID
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace FlyScreen.AppMaui.Platforms.Android;

public sealed class PhoneImeEntryHandler : EntryHandler
{
    protected override MauiAppCompatEditText CreatePlatformView()
        => new PhoneRemoteEditText(Context!);

    protected override void ConnectHandler(MauiAppCompatEditText platformView)
    {
        base.ConnectHandler(platformView);
        if (platformView is not PhoneRemoteEditText edit) return;
        edit.Attach(VirtualView as PhoneImeEntry);
        edit.InstallEditMenu();
    }

    protected override void DisconnectHandler(MauiAppCompatEditText platformView)
    {
        if (platformView is PhoneRemoteEditText edit)
            edit.Attach(null);
        base.DisconnectHandler(platformView);
    }
}
#endif
