using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

internal static class VlcErrorMapper
{
    public static VideoErrorCode FromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return VideoErrorCode.DecodeFailed;

        var m = message.ToLowerInvariant();

        if (m.Contains("401") || m.Contains("unauthorized") || m.Contains("authentication"))
            return VideoErrorCode.AuthRequired;
        if (m.Contains("403") || m.Contains("forbidden"))
            return VideoErrorCode.AuthForbidden;
        if (m.Contains("404") || m.Contains("not found"))
            return VideoErrorCode.SourceNotFound;
        if (m.Contains("timeout") || m.Contains("timed out"))
            return VideoErrorCode.NetTimeout;
        if (m.Contains("no such host") || m.Contains("could not resolve") || m.Contains("name or service not known"))
            return VideoErrorCode.NetDns;
        if (m.Contains("connection refused") || m.Contains("reset"))
            return VideoErrorCode.NetReset;
        if (m.Contains("drm") || m.Contains("unsupported") || m.Contains("no suitable decoder"))
            return VideoErrorCode.SourceUnsupported;
        if (m.Contains("hardware") && m.Contains("fallback"))
            return VideoErrorCode.DecodeHardwareFallback;

        return VideoErrorCode.DecodeFailed;
    }

    public static bool IsNonRetryable(VideoErrorCode code) => code is
        VideoErrorCode.AuthRequired or
        VideoErrorCode.AuthForbidden or
        VideoErrorCode.SourceNotFound or
        VideoErrorCode.SourceUnsupported;
}