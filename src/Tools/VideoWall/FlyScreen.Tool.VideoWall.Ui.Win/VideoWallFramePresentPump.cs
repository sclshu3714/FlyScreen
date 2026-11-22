using System.Windows.Media;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

/// <summary>
/// 全应用共用一个 <see cref="CompositionTarget.Rendering"/>，避免每格各挂一份拖死 UI。
/// </summary>
internal static class VideoWallFramePresentPump
{
    private static readonly object Gate = new();
    private static readonly HashSet<VideoWallFrameSurface> Active = new();
    private static bool _hooked;

    public static void Request(VideoWallFrameSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        lock (Gate)
        {
            Active.Add(surface);
            if (_hooked) return;
            _hooked = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    public static void Unregister(VideoWallFrameSurface surface)
    {
        lock (Gate)
        {
            Active.Remove(surface);
            if (Active.Count > 0 || !_hooked) return;
            _hooked = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        VideoWallFrameSurface[] batch;
        lock (Gate)
            batch = Active.Count == 0 ? [] : Active.ToArray();

        foreach (var s in batch)
            s.PresentIfDirty();
    }
}
