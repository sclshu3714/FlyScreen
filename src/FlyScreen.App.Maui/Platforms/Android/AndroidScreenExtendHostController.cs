#if ANDROID
using FlyScreen.Services;
using FlyScreen.Services.ScreenExtend;
using FlyScreen.AppMaui;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 手机投影主机控制器：系统授权 → 前台采集服务 → HostSessionReady 后推流。
/// 仅「复制」；不做 Sink / 扩展副屏。
/// </summary>
public sealed class AndroidScreenExtendHostController : IDisposable
{
    private readonly ScreenExtendService _service;
    private int _width = 1080;
    private int _height = 1920;
    private bool _prepared;
    private bool _disposed;

    public AndroidScreenExtendHostController(ScreenExtendService service)
    {
        _service = service;
        _service.SessionEnded += OnSessionEnded;
        // 推流由 MediaProjection 服务在 ImageAvailable 时直接 SendFrame；无需 HostSessionReady 再启采集
    }

    public void SetDisplayChoice(ProjectionDisplayChoice? choice)
    {
        if (choice == null) return;
        if (choice.Width > 0) _width = choice.Width;
        if (choice.Height > 0) _height = choice.Height;
    }

    /// <summary>弹出系统「开始录制/投射」同意框并拉起采集服务。</summary>
    public async Task<(bool Ok, string? Error)> PrepareCaptureAsync(CancellationToken ct = default)
    {
        if (_disposed) return (false, "已释放");

        var activity = MainActivity.Current
                       ?? Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity;
        if (activity == null)
            return (false, "无法显示投影授权（Activity 未就绪）");

        var (screenW, screenH, dpi) = ReadPhoneScreen(activity);
        // 虚拟屏必须等于手机真实像素和真实 dpi。改小尺寸或调低 dpi 时，小米会把整帧按比例缩小后居中，四边同时留黑。
        // 弹窗分辨率只限制编码后的长边，缩放在采集端自己做。
        var fitted = ProjectionCaptureSize.ForPhoneScreen(screenW, screenH, _width, _height);
        var capLong = Math.Max(fitted.Width, fitted.Height);
        _width = screenW;
        _height = screenH;

        var grant = await MainActivity.RequestMediaProjectionAsync().ConfigureAwait(true);
        if (!grant.Ok || grant.Data == null)
            return (false, grant.Error ?? "用户取消了屏幕录制授权");

        ct.ThrowIfCancellationRequested();

        var started = await FlyScreenMediaProjectionService
            .StartAsync(_service, grant.ResultCode, grant.Data, screenW, screenH, dpi, capLong)
            .ConfigureAwait(false);
        if (!started)
            return (false, "无法启动屏幕采集服务");

        _prepared = true;
        AppLog.Info($"手机投影采集就绪 {screenW}x{screenH} dpi {dpi} 输出长边 {capLong}");
        return (true, null);
    }

    public Task ReleaseAsync()
    {
        if (!_prepared && FlyScreenMediaProjectionService.Instance == null)
            return Task.CompletedTask;
        _prepared = false;
        FlyScreenMediaProjectionService.Stop();
        return Task.CompletedTask;
    }

    /// <summary>当前朝向的物理像素。GetRealSize 与 Configuration 不一致时以 Configuration 对调。</summary>
    private static (int W, int H, int Dpi) ReadPhoneScreen(global::Android.App.Activity activity)
    {
        var metrics = activity.Resources?.DisplayMetrics;
        var dpi = metrics != null ? (int)metrics.DensityDpi : 320;
        var w = 0;
        var h = 0;
        try
        {
            var size = new global::Android.Graphics.Point();
            global::Android.Views.Display? display = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                display = activity.Display;
            if (display == null)
            {
                var wm = activity.GetSystemService(global::Android.Content.Context.WindowService)
                    as global::Android.Views.IWindowManager;
                display = wm?.DefaultDisplay;
            }

            display?.GetRealSize(size);
            w = size.X;
            h = size.Y;
        }
        catch
        {
            // 旧接口失败时用 DisplayMetrics
        }

        if (w <= 0 || h <= 0)
        {
            w = metrics?.WidthPixels ?? 1080;
            h = metrics?.HeightPixels ?? 1920;
        }

        var orient = activity.Resources?.Configuration?.Orientation
                     ?? global::Android.Content.Res.Orientation.Undefined;
        if (orient == global::Android.Content.Res.Orientation.Portrait && w > h)
            (w, h) = (h, w);
        else if (orient == global::Android.Content.Res.Orientation.Landscape && w < h)
            (w, h) = (h, w);

        return (Math.Max(1, w), Math.Max(1, h), Math.Max(1, dpi));
    }

    private void OnSessionEnded()
    {
        _ = ReleaseAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.SessionEnded -= OnSessionEnded;
        FlyScreenMediaProjectionService.Stop();
    }
}
#endif
