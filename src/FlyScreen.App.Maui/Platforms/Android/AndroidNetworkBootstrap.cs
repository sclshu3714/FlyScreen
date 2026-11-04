#if ANDROID
using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>Android 组播锁 + Wi‑Fi IPv4 辅助（UDP 发现依赖）。</summary>
public static class AndroidNetworkBootstrap
{
    private static WifiManager.MulticastLock? _multicastLock;

    public static void EnsureMulticastLock()
    {
        try
        {
            if (_multicastLock is { IsHeld: true }) return;
            var ctx = global::Android.App.Application.Context;
            if (ctx.GetSystemService(Context.WifiService) is not WifiManager wifi)
                return;
            var lockObj = wifi.CreateMulticastLock("FlyScreenDiscovery");
            if (lockObj == null) return;
            lockObj.SetReferenceCounted(true);
            lockObj.Acquire();
            _multicastLock = lockObj;
        }
        catch
        {
            // 无 Wi‑Fi 服务时忽略
        }
    }

    public static void ReleaseMulticastLock()
    {
        try
        {
            if (_multicastLock is { IsHeld: true })
                _multicastLock.Release();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>优先用 WifiManager 取当前 Wi‑Fi IPv4（比枚举网卡更稳）。</summary>
    public static string? TryGetWifiIpv4()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            // 优先 LinkProperties（API 全版本可用，避免 WifiInfo.IpAddress 在 31+ 过时）
            if (ctx.GetSystemService(Context.ConnectivityService) is ConnectivityManager cm)
            {
                var network = cm.ActiveNetwork;
                var props = network != null ? cm.GetLinkProperties(network) : null;
                if (props?.LinkAddresses != null)
                {
                    foreach (var la in props.LinkAddresses)
                    {
                        var host = la?.Address?.HostAddress;
                        if (string.IsNullOrWhiteSpace(host) || host.Contains(':'))
                            continue;
                        if (LocalPeerIdentity.IsBogusMachineName(host) || host.StartsWith("0."))
                            continue;
                        return host;
                    }
                }
            }

            if (ctx.GetSystemService(Context.WifiService) is not WifiManager wifi)
                return null;
#pragma warning disable CA1422 // ConnectionInfo / IpAddress：仅作旧系统回退
            var info = wifi.ConnectionInfo;
            if (info == null) return null;
            var raw = info.IpAddress;
#pragma warning restore CA1422
            if (raw == 0) return null;
            // Android 返回小端 int
            var b0 = raw & 0xFF;
            var b1 = (raw >> 8) & 0xFF;
            var b2 = (raw >> 16) & 0xFF;
            var b3 = (raw >> 24) & 0xFF;
            var ip = $"{b0}.{b1}.{b2}.{b3}";
            if (LocalPeerIdentity.IsBogusMachineName(ip) || ip.StartsWith("0."))
                return null;
            return ip;
        }
        catch
        {
            return null;
        }
    }
}
#endif
