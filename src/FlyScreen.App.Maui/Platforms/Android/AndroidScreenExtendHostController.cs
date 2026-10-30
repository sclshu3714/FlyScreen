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

        var metrics = activity.Resources?.DisplayMetrics;
        var screenW = metrics?.WidthPixels ?? 1080;
        var screenH = metrics?.HeightPixels ?? 1920;
        var dpi = metrics != null ? (int)metrics.DensityDpi : 320;

        // 弹窗分辨率：未指定则用物理像素；过大则限制上限减轻带宽
        var w = _width > 0 ? _width : screenW;
        var h = _height > 0 ? _height : screenH;
        const int maxEdge = 1920;
        if (w > maxEdge || h > maxEdge)
        {
            var scale = Math.Min(maxEdge / (double)w, maxEdge / (double)h);
            w = Math.Max(160, (int)(w * scale));
            h = Math.Max(160, (int)(h * scale));
        }

        // 保持竖/横与本机一致：若用户选了横向分辨率但手机竖屏，仍按物理方向推
        if (screenW < screenH && w > h)
            (w, h) = (h, w);
        else if (screenW > screenH && w < h)
            (w, h) = (h, w);

        _width = w;
        _height = h;

        var grant = await MainActivity.RequestMediaProjectionAsync().ConfigureAwait(true);
        if (!grant.Ok || grant.Data == null)
            return (false, grant.Error ?? "用户取消了屏幕录制授权");

        ct.ThrowIfCancellationRequested();

        var started = await FlyScreenMediaProjectionService
            .StartAsync(_service, grant.ResultCode, grant.Data, w, h, dpi)
            .ConfigureAwait(false);
        if (!started)
            return (false, "无法启动屏幕采集服务");

        _prepared = true;
        AppLog.Info($"手机投影采集就绪 {w}x{h}");
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
