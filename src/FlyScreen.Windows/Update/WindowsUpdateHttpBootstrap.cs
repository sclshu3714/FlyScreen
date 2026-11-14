using System.Net;
using System.Net.Http;
using FlyScreen.Services.Update;

namespace FlyScreen.Update;

/// <summary>
/// Win10 上默认 SocketsHttpHandler 异步 DNS 偶发「不知道这样的主机」。
/// 启动时改走 WinHTTP + WinINet 代理/DNS，与浏览器更一致。
/// </summary>
public static class WindowsUpdateHttpBootstrap
{
    private static int _done;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0)
            return;

        GitHubReleaseClient.MessageHandlerFactory = static () => new WinHttpHandler
        {
            WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinInetProxy,
            AutomaticDecompression = DecompressionMethods.All,
            SendTimeout = TimeSpan.FromMinutes(30),
            ReceiveHeadersTimeout = TimeSpan.FromSeconds(45),
            ReceiveDataTimeout = TimeSpan.FromMinutes(30)
        };
    }
}
