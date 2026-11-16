namespace FlyScreen.Tool.VideoWall.Contracts;

/// <summary>播放态 → UI 表现辅助（加载遮罩等）。</summary>
public static class TilePlayStateUi
{
    /// <summary>
    /// 是否显示加载遮罩。
    /// 已出画后不再遮罩（直播 RTSP 会持续 Buffering，不能盖住画面）。
    /// </summary>
    public static bool ShowsLoading(TilePlayState state, bool hasPresentedFrame = false)
    {
        if (hasPresentedFrame)
            return false;

        return state is TilePlayState.Resolving or TilePlayState.Connecting
            or TilePlayState.Buffering or TilePlayState.Stalled
            or TilePlayState.Reconnecting;
    }
}
