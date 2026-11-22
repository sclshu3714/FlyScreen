using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using LibVLCSharp.Shared;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

internal static class VlcMrlHelper
{
    public static Media CreateMedia(LibVLC libVlc, VideoEndpoint endpoint, MediaPlayerOptions options, CredentialStore? credentials)
    {
        // 本机文件走 FromPath（支持中文路径）；网络地址走 FromLocation
        Media media;
        if (TryGetLocalFilePath(endpoint, out var localPath))
            media = new Media(libVlc, localPath, FromType.FromPath);
        else if (NeedsOwnedHttp(endpoint))
        {
            // LibVLC 3 的 HTTPS 模块发不出自定义头，带 Cookie / UG-Agent 的地址改由自有客户端拉流
            var input = new RangeHttpMediaInput(new Uri(BuildMrl(endpoint)), endpoint.Headers);
            try
            {
                media = new Media(libVlc, input);
            }
            catch
            {
                input.Dispose();
                throw;
            }
        }
        else
            media = new Media(libVlc, BuildMrl(endpoint), FromType.FromLocation);

        ApplyOptions(media, endpoint, options, credentials);
        return media;
    }

    private static bool NeedsOwnedHttp(VideoEndpoint endpoint)
    {
        var protocol = (endpoint.Protocol ?? "").Trim().ToLowerInvariant();
        if (protocol is not ("http" or "https"))
            return false;

        // 只有 LibVLC 3 发不出去的头才改走自有客户端；Referer / User-Agent 仍交给 :http-referrer / :http-user-agent
        foreach (var (key, value) in endpoint.Headers)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            if (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                || key.Equals("UG-Agent", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string BuildMrl(VideoEndpoint endpoint)
    {
        var protocol = (endpoint.Protocol ?? "").Trim().ToLowerInvariant();
        var address = endpoint.Address.Trim();

        if (protocol == "file")
        {
            if (address.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return address;

            return new Uri(address).AbsoluteUri;
        }

        if (address.Contains("://", StringComparison.Ordinal))
            return address;

        return $"{protocol}://{address}";
    }

    private static bool TryGetLocalFilePath(VideoEndpoint endpoint, out string path)
    {
        path = "";
        var protocol = (endpoint.Protocol ?? "").Trim().ToLowerInvariant();
        var address = (endpoint.Address ?? "").Trim();
        if (address.Length == 0) return false;

        if (protocol == "file" || LooksLikeWindowsPath(address) || LooksLikeUnixAbsolutePath(address))
        {
            if (address.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { path = new Uri(address).LocalPath; }
                catch { return false; }
            }
            else
            {
                path = address;
            }

            return path.Length > 0;
        }

        return false;
    }

    private static bool LooksLikeWindowsPath(string s)
        => s.Length >= 3
           && char.IsLetter(s[0])
           && s[1] == ':'
           && (s[2] == '\\' || s[2] == '/');

    private static bool LooksLikeUnixAbsolutePath(string s)
        => s.Length >= 2 && s[0] == '/' && s[1] != '/';

    private static void ApplyOptions(
        Media media,
        VideoEndpoint endpoint,
        MediaPlayerOptions options,
        CredentialStore? credentials)
    {
        var protocol = (endpoint.Protocol ?? "").Trim().ToLowerInvariant();

        if (protocol == "rtsp")
        {
            var transport = (endpoint.Transport ?? "tcp").Trim().ToLowerInvariant();
            if (transport is "" or "tcp")
                media.AddOption(":rtsp-tcp");
        }

        var decode = (options.DecodePreference ?? "auto").Trim().ToLowerInvariant();

        // Android 无 d3d11va；软帧回调路径强制 CPU 解码更稳
        if (OperatingSystem.IsAndroid())
        {
            media.AddOption(":avcodec-hw=none");
        }
        else
        {
            media.AddOption(decode switch
            {
                "hardware" => ":avcodec-hw=d3d11va",
                "software" => ":avcodec-hw=none",
                _ => ":avcodec-hw=any"
            });
        }

        var bufferMs = options.BufferMs ?? endpoint.BufferMs;
        if (bufferMs is > 0)
            media.AddOption($":network-caching={bufferMs.Value}");

        if (!string.IsNullOrWhiteSpace(endpoint.CredentialRef) && credentials is not null)
        {
            var cred = credentials.Get(endpoint.CredentialRef);
            if (cred is not null)
            {
                if (!string.IsNullOrEmpty(cred.Username))
                {
                    if (protocol is "rtsp" or "rtmp" or "rtmps")
                    {
                        media.AddOption($":rtsp-user={cred.Username}");
                        media.AddOption($":rtsp-pwd={cred.Password}");
                    }
                    else if (protocol is "http" or "https" or "hls" or "dash")
                    {
                        media.AddOption($":http-user={cred.Username}");
                        media.AddOption($":http-pwd={cred.Password}");
                    }
                }
            }
        }

        foreach (var (key, value) in endpoint.Headers)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Referrer", StringComparison.OrdinalIgnoreCase))
                media.AddOption($":http-referrer={value}");
            else if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                media.AddOption($":http-user-agent={value}");
            else
                media.AddOption($":http-header={key}: {value}");
        }
    }
}