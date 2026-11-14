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
        else
            media = new Media(libVlc, BuildMrl(endpoint), FromType.FromLocation);

        ApplyOptions(media, endpoint, options, credentials);
        return media;
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

        if (protocol == "file" || LooksLikeWindowsPath(address))
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

        media.AddOption(decode switch
        {
            "hardware" => ":avcodec-hw=d3d11va",
            "software" => ":avcodec-hw=none",
            _ => ":avcodec-hw=any"
        });

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
            media.AddOption($":http-header={key}: {value}");
        }
    }
}