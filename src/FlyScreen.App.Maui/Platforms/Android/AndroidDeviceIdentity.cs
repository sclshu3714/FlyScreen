#if ANDROID
using Android.OS;
using Android.Provider;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>解析对端可见的手机/平板名称（避免 Environment.MachineName=localhost）。</summary>
public static class AndroidDeviceIdentity
{
    public static string? ResolveFriendlyName()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            // 用户在系统设置里改过的设备名（Android 7.1+）
            // Settings.Global.DEVICE_NAME = "device_name"（部分绑定无 DeviceName 常量）
            var deviceName = Settings.Global.GetString(ctx.ContentResolver, "device_name");
            if (IsUsable(deviceName))
                return deviceName!.Trim();
        }
        catch
        {
            // ignore
        }

        try
        {
            var bluetooth = Settings.Secure.GetString(
                global::Android.App.Application.Context.ContentResolver, "bluetooth_name");
            if (IsUsable(bluetooth))
                return bluetooth!.Trim();
        }
        catch
        {
            // ignore
        }

        try
        {
            var model = Build.Model?.Trim() ?? "";
            var manufacturer = Build.Manufacturer?.Trim() ?? "";
            if (IsUsable(model))
            {
                if (IsUsable(manufacturer)
                    && !model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
                    return $"{manufacturer} {model}";
                return model;
            }
        }
        catch
        {
            // ignore
        }

        return "Android";
    }

    private static bool IsUsable(string? name)
        => !string.IsNullOrWhiteSpace(name) && !LocalPeerIdentity.IsBogusMachineName(name);
}
#endif
