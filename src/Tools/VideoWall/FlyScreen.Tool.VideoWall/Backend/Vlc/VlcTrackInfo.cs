using LibVLCSharp.Shared;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

/// <summary>从 <see cref="Media"/> 轨道中提取首路视频信息（MediaTrack 为 struct，避免可空链式误用）。</summary>
internal static class VlcTrackInfo
{
    public readonly struct VideoInfo
    {
        public VideoInfo(string? codec, int width, int height, double fps)
        {
            Codec = codec;
            Width = width;
            Height = height;
            Fps = fps;
        }

        public string? Codec { get; }
        public int Width { get; }
        public int Height { get; }
        public double Fps { get; }
    }

    public static bool TryGetVideo(Media? media, out VideoInfo info)
    {
        info = default;
        if (media is null) return false;

        MediaTrack[]? tracks;
        try
        {
            tracks = media.Tracks;
        }
        catch
        {
            return false;
        }

        if (tracks is null || tracks.Length == 0) return false;

        foreach (var track in tracks)
        {
            if (track.TrackType != TrackType.Video) continue;

            var w = (int)track.Data.Video.Width;
            var h = (int)track.Data.Video.Height;
            var den = Math.Max(1u, track.Data.Video.FrameRateDen);
            var fps = track.Data.Video.FrameRateNum / (double)den;
            info = new VideoInfo(FourCcToString(track.Codec), w, h, fps);
            return true;
        }

        return false;
    }

    private static string? FourCcToString(uint fourCc)
    {
        if (fourCc == 0) return null;
        var c0 = (char)(fourCc & 0xFF);
        var c1 = (char)((fourCc >> 8) & 0xFF);
        var c2 = (char)((fourCc >> 16) & 0xFF);
        var c3 = (char)((fourCc >> 24) & 0xFF);
        return new string([c0, c1, c2, c3]).Trim('\0', ' ');
    }
}
