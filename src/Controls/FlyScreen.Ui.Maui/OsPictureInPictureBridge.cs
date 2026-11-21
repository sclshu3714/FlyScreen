namespace FlyScreen.Ui.Maui;

/// <summary>
/// 宿主 Android Activity ↔ 工具 UI 的系统画中画桥接（避免工具程序集反向引用 App）。
/// 进入 PiP 前须把「仅视频」铺满 Activity，否则系统会把顶栏/应用/退出一并缩进小窗。
/// </summary>
public static class OsPictureInPictureBridge
{
    public static event Action<bool>? ModeChanged;

    /// <summary>宿主：把视频 View 挂到全屏并隐藏壳层；失败返回 false。</summary>
    public static Func<View, bool>? PresentVideoFullscreen { get; set; }

    /// <summary>宿主：还原壳层与视频原父级。</summary>
    public static Action? DismissVideoFullscreen { get; set; }

    public static bool TryPresentVideoFullscreen(View video)
        => PresentVideoFullscreen?.Invoke(video) ?? false;

    public static void DismissPresentedVideo()
        => DismissVideoFullscreen?.Invoke();

    public static void NotifyModeChanged(bool isInPictureInPicture)
    {
        if (!isInPictureInPicture)
            DismissPresentedVideo();
        ModeChanged?.Invoke(isInPictureInPicture);
    }
}
