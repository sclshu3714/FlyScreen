using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using LibVLCSharp.Shared;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

internal static class VlcMrlHelper
{
    public static Media CreateMedia(LibVLC libVlc, VideoEndpoint endpoint, MediaPlayerOptions options, CredentialStore? credentials)
    {
        var mrl = BuildMrl(endpoint);
        var media = new Media(libVlc, mrl, FromType.FromLocation);
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