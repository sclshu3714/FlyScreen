#if ANDROID
using FlyScreen.AppMaui.Services;
using FlyScreen.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.ScreenExtend;
using FlyScreen.Models;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// Android 键鼠接收：
/// 1) 悬浮小窗画指针（其他应用上层可见）
/// 2) 未加窗成功前绝不关掉无障碍/应用内箭头（避免「能点但看不见」）
/// 3) 电脑主动穿入时解除触控板屏蔽
/// 4) 手机→电脑复制投影（MediaProjection Host，不做 Sink）
/// </summary>
public sealed class AndroidMauiPlatformServices : IMauiPlatformServices
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly MauiClipboardApplySink _clipSink = new();
    private readonly AndroidScreenExtendHostController _screenExtendHost;
    private CancellationTokenSource? _clipCts;
    private string? _lastInlineKey;
    private string? _lastFilesKey;
    private bool _receiving;
    private bool _promptedOverlay;
    private bool _promptedTouchInjection;
    private bool _remotePointerSuppressed;
    private bool _touchpadWantsSuppress;
    private string? _lastOverlayWarn;
    private long _lastOverlayWarnTicks;
    private bool? _a11yDrawCursor;

    public AndroidMauiPlatformServices(
        AppSettings settings,
        FileTransferService transfers,
        ScreenExtendService screenExtend)
    {
        _settings = settings;
        _transfers = transfers;
        _screenExtendHost = new AndroidScreenExtendHostController(screenExtend);
        // 用户程序运行：WakeLock；可选「通知注入」前台通知（无子程序则不占通知）
        ProgramService.Instance.Runner.RunningCountChanged += count =>
            AndroidProgramWakeLock.SetProgramsRunning(count);
    }

    /// <summary>弹出录屏授权并启动采集；须在 StartHostAsync 之前调用。</summary>
    public Task<(bool Ok, string? Error)> PrepareScreenExtendCaptureAsync(CancellationToken ct = default)
        => _screenExtendHost.PrepareCaptureAsync(ct);

    public IClipboardApplySink ClipboardSink => _clipSink;
    public bool SupportsDesktopInput => false;
    public bool SupportsTray => false;
    public bool SupportsAutoStart => false;
    public bool IsFocusOnLocalMachine => !_receiving;
    public bool AcceptsRemoteInput => _receiving && !_remotePointerSuppressed;

    public event Action<string?>? ControlTargetChanged;

    public void SetRemotePointerSuppressed(bool suppressed)
    {
        _touchpadWantsSuppress = suppressed;
        ApplySuppressState();
    }

    /// <summary>Activity OnPause：退后台时允许电脑控制本机并显示指针。</summary>
    public void NotifyAppBackgrounded()
    {
        if (!_remotePointerSuppressed) return;
        _remotePointerSuppressed = false;
        AppLog.Info("已退到后台：允许电脑键鼠穿入并显示指针");
    }

    /// <summary>Activity OnResume：若仍在触控板页，恢复屏蔽。</summary>
    public void NotifyAppForegrounded()
    {
        FlyScreenCursorOverlay.InvalidateCanDrawCache();
        ApplySuppressState();
    }

    private void ApplySuppressState()
    {
        // 触控板页优先：屏蔽电脑键鼠，避免双指针；离开触控板后再允许穿入
        var want = _touchpadWantsSuppress;
        if (_remotePointerSuppressed == want) return;
        _remotePointerSuppressed = want;
        if (want && _receiving)
        {
            EndRemoteCursor();
            ControlTargetChanged?.Invoke(null);
            AppLog.Info("触控板已启用：已断开电脑键鼠控制");
        }
    }

    public void StartDesktopInput() { }
    public void StopDesktopInput() { }
    public void SetEasyMouse(bool enabled) { }

    public void InjectMouseMoveAbsolute(double nx, double ny)
    {
        if (_remotePointerSuppressed || !_receiving) return;

        // 无障碍只更新点击坐标，不画（画交给系统悬浮窗）
        if (FlyScreenInputAccessibilityService.IsConnected)
            FlyScreenInputAccessibilityService.Instance?.MoveAbsolute(nx, ny);

        UpdateCursorVisual(nx, ny, relative: false, 0, 0);
    }

    public void InjectMouseMoveRelative(int dx, int dy)
    {
        if (_remotePointerSuppressed || !_receiving) return;

        if (FlyScreenInputAccessibilityService.IsConnected)
            FlyScreenInputAccessibilityService.Instance?.MoveRelative(dx, dy);

        UpdateCursorVisual(0, 0, relative: true, dx, dy);
    }

    public void InjectMouseButton(int button, bool down)
    {
        if (_remotePointerSuppressed || !_receiving) return;
        // 组字浮层可见时：点空白取消词条（点在候选条上则保留，供选词）
        if (down)
            RemoteCompositionOverlay.DismissIfPointerOutside();
        InjectTouch(() => FlyScreenInputAccessibilityService.Instance?.Click(button, down));
    }

    public void InjectMouseWheel(int delta, bool horizontal)
    {
        if (_remotePointerSuppressed || !_receiving) return;
        InjectTouch(() => FlyScreenInputAccessibilityService.Instance?.Wheel(delta, horizontal));
    }

    private bool _shiftKey;
    private bool _capsKey;

    public void InjectKey(ushort vk, bool down, bool extended)
    {
        if (_remotePointerSuppressed || !_receiving) return;
        SoftKeyboardGuard.Hide();

        // 无障碍已开：完整 IME + 手势路径（跨屏字母由电脑组字，此处多为功能键）
        if (FlyScreenInputAccessibilityService.IsConnected)
        {
            FlyScreenInputAccessibilityService.Instance?.Key(vk, down);
            return;
        }

        // 无障碍未开：程序编辑器仍可直写 Monaco
        TrackModifierKeys(vk, down);
        if (RemoteTextInputHub.HasSink
            && RemoteTextInputHub.TryHandleRemoteKey(vk, down, _shiftKey, _capsKey, ctrl: false))
            return;

        PromptTouchInjectionOnce();
    }

    public void InjectText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SoftKeyboardGuard.Hide();

        // 编辑器 sink 优先：不依赖被控态，避免 TextInput 被 _receiving 门闩丢掉
        if (RemoteTextInputHub.TryAcceptText(text))
            return;

        if (_remotePointerSuppressed || !_receiving) return;

        if (FlyScreenInputAccessibilityService.IsConnected)
        {
            FlyScreenInputAccessibilityService.Instance?.InsertCommittedText(text);
            return;
        }

        PromptTouchInjectionOnce();
    }

    private void TrackModifierKeys(ushort vk, bool down)
    {
        switch (vk)
        {
            case 0x10 or 0xA0 or 0xA1:
                _shiftKey = down;
                break;
            case 0x14:
                if (down) _capsKey = !_capsKey;
                break;
        }
    }

    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        // 触控板正在控制电脑时，拒绝被电脑反控
        if (_touchpadWantsSuppress)
        {
            AppLog.Info("触控板模式：忽略电脑键鼠穿入");
            return;
        }

        _remotePointerSuppressed = false;
        _receiving = true;
        _a11yDrawCursor = null;
        FlyScreenCursorOverlay.InvalidateCanDrawCache();

        // 禁止应用内 DecorView 箭头——它只活在玄鸟窗口里，退后台必消失
        VirtualCursorOverlay.Hide();

        var canOverlay = FlyScreenCursorOverlay.CanDraw();
        if (FlyScreenInputAccessibilityService.IsReady)
        {
            // 有悬浮窗权限时不画无障碍箭头，避免双指针乱跳
            SetA11yDrawCursor(!canOverlay);
            FlyScreenInputAccessibilityService.Instance!.BeginSession(entryX, entryY);
        }
        else
            PromptTouchInjectionOnce();

        if (canOverlay)
            FlyScreenCursorOverlay.ShowAt(entryX, entryY);
        else
            PromptOverlayOnce();

        UpdateCursorVisual(entryX, entryY, relative: false, 0, 0);
        // 跨屏打字由电脑后台组字，手机只显示候选条，禁止本机软键盘
        SoftKeyboardGuard.SuppressWhileReceiving = true;
        SoftKeyboardGuard.Hide();

        ControlTargetChanged?.Invoke(null);
        AppLog.Info($"开始接收电脑键鼠 @ ({entryX:0.##},{entryY:0.##}) overlay={FlyScreenCursorOverlay.CanDraw()} showing={FlyScreenCursorOverlay.IsShowing} a11y={FlyScreenInputAccessibilityService.IsConnected}");
    }

    public void OnCursorOwnerChanged(string ownerMachine)
    {
        if (string.IsNullOrWhiteSpace(ownerMachine)) return;
        var local = string.Equals(ownerMachine, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ownerMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        if (local) return;
        if (_receiving)
            EndRemoteCursor();
    }

    public void RestoreCursorWhenLeavingCluster()
    {
        if (!_receiving) return;
        EndRemoteCursor();
        ControlTargetChanged?.Invoke(null);
    }

    public void StartClipboardMonitor()
    {
        _clipCts = new CancellationTokenSource();
        _ = LoopAsync(_clipCts.Token);
    }

    public void StopClipboardMonitor()
    {
        try { _clipCts?.Cancel(); } catch { /* ignore */ }
        _clipCts?.Dispose();
        _clipCts = null;
    }

    public void ArmRemoteClipboardForPaste()
    {
        if (!_settings.ShareClipboard) return;
        if (_transfers.HasRemotePendingInline)
            _ = ArmInlineAsync();
        // 与 Win 对齐：宣告时不拉取；由「接收剪贴板文件」显式拉取
    }


    public bool GetAutoStartEnabled() => false;
    public void SetAutoStartEnabled(bool enabled) { }

    public void EnsureTray(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null) { }

    public void HideToBackground(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null)
    {
        var activity = Platform.CurrentActivity;
        activity?.MoveTaskToBack(true);
    }

    public void DisposeTray() { }

    public Task<(bool Ok, string? InfoOrError)> EnsureScreenExtendHostDisplayAsync(
        LayoutEdgeSide layoutSide = LayoutEdgeSide.Unknown)
        // 手机仅复制，无虚拟副屏；采集在 PrepareScreenExtendCaptureAsync
        => Task.FromResult<(bool, string?)>((true, "手机复制投影"));

    public Task ReleaseScreenExtendHostDisplayAsync()
        => _screenExtendHost.ReleaseAsync();

    public void SetScreenExtendProjectionMode(DisplayProjectionMode mode) { }

    public void SetScreenExtendDisplayChoice(ProjectionDisplayChoice? choice)
        => _screenExtendHost.SetDisplayChoice(choice);

    /// <summary>
    /// 指针只走系统层：悬浮窗 或 无障碍 Overlay。
    /// 绝不走 VirtualCursorOverlay（DecorView），否则退后台/多任务预览里像「画在玄鸟里面」。
    /// 热路径：不每帧 Hide / 不每帧查权限 / 不每帧 SetDrawCursor。
    /// </summary>
    private void UpdateCursorVisual(double nx, double ny, bool relative, int dx, int dy)
    {
        if (FlyScreenCursorOverlay.CanDraw())
        {
            if (!FlyScreenCursorOverlay.IsShowing)
                FlyScreenCursorOverlay.ShowAt(relative ? 0.5 : nx, relative ? 0.5 : ny);

            if (relative)
                FlyScreenCursorOverlay.MoveRelative(dx, dy);
            else
                FlyScreenCursorOverlay.MoveAbsolute(nx, ny);

            if (FlyScreenCursorOverlay.IsShowing)
            {
                SetA11yDrawCursor(false);
                return;
            }

            // 悬浮窗尚未加上：短暂用无障碍层顶上（仍可盖其他应用）
            var err = FlyScreenCursorOverlay.LastError;
            if (!string.IsNullOrEmpty(err))
            {
                var now = Environment.TickCount64;
                if (!string.Equals(err, _lastOverlayWarn, StringComparison.Ordinal) || now - _lastOverlayWarnTicks > 5000)
                {
                    _lastOverlayWarn = err;
                    _lastOverlayWarnTicks = now;
                    AppLog.Warn($"系统悬浮指针未就绪: {err}");
                }
            }
        }
        else
        {
            PromptOverlayOnce();
        }

        // 唯一备用：无障碍系统 Overlay（不要 VirtualCursorOverlay）
        SetA11yDrawCursor(true);
    }

    private void SetA11yDrawCursor(bool enabled)
    {
        if (_a11yDrawCursor == enabled) return;
        _a11yDrawCursor = enabled;
        FlyScreenInputAccessibilityService.Instance?.SetDrawCursor(enabled);
    }

    private void EndRemoteCursor()
    {
        _receiving = false;
        _a11yDrawCursor = null;
        SoftKeyboardGuard.SuppressWhileReceiving = false;
        VirtualCursorOverlay.Hide();
        RemoteCompositionOverlay.DismissAndCancel();
        FlyScreenCursorOverlay.End();
        FlyScreenInputAccessibilityService.Instance?.EndSession();
        ApplySuppressState();
    }

    private void InjectTouch(Action inject)
    {
        if (FlyScreenInputAccessibilityService.IsReady)
        {
            inject();
            return;
        }
        PromptTouchInjectionOnce();
    }

    private void PromptOverlayOnce()
    {
        if (_promptedOverlay) return;
        _promptedOverlay = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null)
            {
                OpenOverlaySettings();
                return;
            }

            var go = await page.DisplayAlertAsync(
                "需要悬浮窗权限",
                "电脑鼠标控制手机时要看到指针，请允许「显示在其他应用上层」。",
                "去开启",
                "稍后");
            if (go)
                OpenOverlaySettings();
        });
    }

    private void PromptTouchInjectionOnce()
    {
        if (_promptedTouchInjection) return;
        _promptedTouchInjection = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null)
            {
                FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
                return;
            }

            var go = await page.DisplayAlertAsync(
                "模拟点击需要系统接口",
                "在其他应用上点击/滑动需要开启「玄鸟键鼠注入」无障碍服务。\n也可在「高级 → 行为 → 无障碍注入」中开启（仅手机、无电脑时同样可用）。",
                "去开启",
                "稍后");
            if (go)
                FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
        });
    }

    private static void OpenOverlaySettings()
    {
        var ctx = global::Android.App.Application.Context;
        if (ctx == null) return;
        var uri = global::Android.Net.Uri.Parse("package:" + ctx.PackageName);
        var intent = new global::Android.Content.Intent(
            global::Android.Provider.Settings.ActionManageOverlayPermission, uri);
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        ctx.StartActivity(intent);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1200, ct).ConfigureAwait(false);
                if (!_settings.ShareClipboard || _clipSink.IsSuppressed) continue;

                if (_settings.TransferFile)
                {
                    var files = await MainThread.InvokeOnMainThreadAsync(TryCaptureAndroidFilePaths)
                        .ConfigureAwait(false);
                    if (files is { Count: > 0 })
                    {
                        var real = files.Where(p => !_transfers.IsInboxPath(p)).ToList();
                        if (real.Count > 0)
                        {
                            var key = string.Join("|", real);
                            if (key != _lastFilesKey
                                && await _transfers.AnnounceLocalFilesAsync(real).ConfigureAwait(false))
                                _lastFilesKey = key;
                            continue;
                        }
                    }
                }

                var inline = await MainThread.InvokeOnMainThreadAsync(TryCaptureAndroidInline)
                    .ConfigureAwait(false);
                if (inline == null || inline.IsEmpty) continue;
                if (_clipSink.IsSameAsLastApplied(inline)) continue;

                var inlineKey = inline.BuildKey();
                if (inlineKey == _lastInlineKey) continue;
                await _transfers.AnnounceLocalInlineAsync(inline).ConfigureAwait(false);
                _lastInlineKey = inlineKey;
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore */ }
        }
    }

    private static ClipboardInlineData? TryCaptureAndroidInline()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            if (ctx == null) return null;
            var cm = ctx.GetSystemService(global::Android.Content.Context.ClipboardService)
                as global::Android.Content.ClipboardManager;
            var clip = cm?.PrimaryClip;
            if (clip == null || clip.ItemCount <= 0) return null;

            string? text = null;
            string? html = null;
            byte[]? png = null;

            for (var i = 0; i < clip.ItemCount; i++)
            {
                var item = clip.GetItemAt(i);
                if (item == null) continue;

                if (string.IsNullOrEmpty(html))
                {
                    try
                    {
                        html = item.HtmlText;
                        if (string.IsNullOrEmpty(html)) html = null;
                    }
                    catch
                    {
                        html = null;
                    }
                }

                if (string.IsNullOrEmpty(text))
                {
                    text = item.Text;
                    if (string.IsNullOrEmpty(text))
                        text = item.CoerceToText(ctx)?.ToString();
                    if (string.IsNullOrEmpty(text)) text = null;
                }

                if (png == null)
                {
                    var uri = item.Uri;
                    if (uri != null && IsLikelyImageUri(ctx, uri))
                        png = TryReadImagePng(ctx, uri);
                }
            }

            // 输入法编辑条用的占位符，不能当成用户剪贴板同步到电脑
            if (PhoneRemoteEditText.IsRemoteOnlyText(text))
                text = null;

            var data = new ClipboardInlineData { Text = text, Html = html, Png = png };
            return data.IsEmpty ? null : data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>非图片 URI → 物化到缓存后宣告为文件（TransferFile 开启时）。</summary>
    private static IReadOnlyList<string>? TryCaptureAndroidFilePaths()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            if (ctx == null) return null;
            var cm = ctx.GetSystemService(global::Android.Content.Context.ClipboardService)
                as global::Android.Content.ClipboardManager;
            var clip = cm?.PrimaryClip;
            if (clip == null || clip.ItemCount <= 0) return null;

            var paths = new List<string>();
            for (var i = 0; i < clip.ItemCount; i++)
            {
                var item = clip.GetItemAt(i);
                var uri = item?.Uri;
                if (uri == null) continue;
                if (IsLikelyImageUri(ctx, uri)) continue;

                var path = TryMaterializeUriToCache(ctx, uri);
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }

            return paths.Count > 0 ? paths : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLikelyImageUri(global::Android.Content.Context ctx, global::Android.Net.Uri uri)
    {
        try
        {
            var mime = ctx.ContentResolver?.GetType(uri);
            if (!string.IsNullOrEmpty(mime) && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return true;

            var path = uri.Path ?? uri.ToString() ?? "";
            return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryMaterializeUriToCache(
        global::Android.Content.Context ctx, global::Android.Net.Uri uri)
    {
        try
        {
            if (string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(uri.Path)
                && File.Exists(uri.Path))
                return uri.Path;

            using var input = ctx.ContentResolver?.OpenInputStream(uri);
            if (input == null) return null;

            var name = uri.LastPathSegment;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/'))
                name = "clip-" + Guid.NewGuid().ToString("N");

            var dir = Path.Combine(ctx.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "flyscreen-clip");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            using (var fs = File.Create(path))
                input.CopyTo(fs);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryReadImagePng(global::Android.Content.Context ctx, global::Android.Net.Uri uri)
    {
        try
        {
            using var input = ctx.ContentResolver?.OpenInputStream(uri);
            if (input == null) return null;

            using var bmp = global::Android.Graphics.BitmapFactory.DecodeStream(input);
            if (bmp == null) return null;

            using var ms = new MemoryStream();
            if (!bmp.Compress(global::Android.Graphics.Bitmap.CompressFormat.Png!, 100, ms))
                return null;
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private async Task ArmInlineAsync()
    {
        try
        {
            var data = await _transfers.PullRemoteInlineForPasteAsync().ConfigureAwait(false);
            if (data == null || data.IsEmpty) return;
            MainThread.BeginInvokeOnMainThread(() => _clipSink.ApplyInline(data));
        }
        catch { /* ignore */ }
    }
}
#endif
