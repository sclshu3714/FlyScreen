#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using FlyScreen.Services;
using Path = Android.Graphics.Path;
using View = Android.Views.View;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 前台服务持有系统悬浮指针窗。加在 Service 的 WindowManager 上，
/// 不绑 Activity，退到后台/切应用后仍可显示。
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[MetaData("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE",
    Value = "Remote PC mouse cursor overlay while receiving control")]
public sealed class FlyScreenCursorOverlayService : Service
{
    public const string ActionEnsure = "flyscreen.cursor.ENSURE";
    public const string ActionEnd = "flyscreen.cursor.END";
    private const int NotifyId = 42;
    private const string ChannelId = "flyscreen.cursor";
    private const float CursorDp = 40f;
    private const double RefW = 1920;
    private const double RefH = 1080;

    public static FlyScreenCursorOverlayService? Instance { get; private set; }

    private static readonly object Gate = new();
    private static bool _wantVisible;
    private static double _nx = 0.5;
    private static double _ny = 0.5;
    private static bool _scheduled;
    private static string? _lastError;
    private static int _failCount;

    private Handler? _ui;
    private Context? _windowCtx;
    private IWindowManager? _wm;
    private PointerView? _cursor;
    private WindowManagerLayoutParams? _lp;
    private int _screenW = 1080;
    private int _screenH = 1920;
    private int _cursorPx = 80;
    private bool _added;
    private bool _foregrounded;

    public static bool IsShowing
    {
        get { lock (Gate) return _wantVisible && Instance?._added == true; }
    }

    public static string? LastError
    {
        get { lock (Gate) return _lastError; }
    }

    public static void RequestShow(double nx, double ny)
    {
        lock (Gate)
        {
            _wantVisible = true;
            _nx = Math.Clamp(nx, 0, 1);
            _ny = Math.Clamp(ny, 0, 1);
        }
        StartOrSignal(ActionEnsure);
    }

    public static void RequestMoveAbsolute(double nx, double ny)
    {
        lock (Gate)
        {
            if (!_wantVisible) return;
            _nx = Math.Clamp(nx, 0, 1);
            _ny = Math.Clamp(ny, 0, 1);
        }
        if (Instance != null)
            Instance.ScheduleFlush();
        else
            StartOrSignal(ActionEnsure);
    }

    public static void RequestMoveRelative(int dx, int dy)
    {
        lock (Gate)
        {
            if (!_wantVisible) return;
            _nx = Math.Clamp(_nx + dx / RefW, 0, 1);
            _ny = Math.Clamp(_ny + dy / RefH, 0, 1);
        }
        if (Instance != null)
            Instance.ScheduleFlush();
        else
            StartOrSignal(ActionEnsure);
    }

    public static void RequestEnd()
    {
        lock (Gate)
            _wantVisible = false;
        if (Instance != null)
            StartOrSignal(ActionEnd);
    }

    private static void StartOrSignal(string action)
    {
        var ctx = global::Android.App.Application.Context;
        if (ctx == null) return;

        var intent = new Intent(ctx, typeof(FlyScreenCursorOverlayService));
        intent.SetAction(action);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                ctx.StartForegroundService(intent);
            else
                ctx.StartService(intent);
        }
        catch (Exception ex)
        {
            lock (Gate)
                _lastError = ex.Message;
            Log.Warn("FlyScreen", "cursor FGS start: " + ex.Message);
            AppLog.Warn($"指针前台服务启动失败: {ex.Message}");
        }
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        _ui = new Handler(Looper.MainLooper!);
        EnterForeground();
        Log.Info("FlyScreen", "cursor overlay service created");
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnterForeground();

        var action = intent?.Action;
        if (string.Equals(action, ActionEnd, StringComparison.Ordinal))
        {
            lock (Gate)
                _wantVisible = false;
            Flush();
            StopSelfSafe();
            return StartCommandResult.NotSticky;
        }

        ScheduleFlush();
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        Hide();
        ResetView();
        _windowCtx = null;
        if (ReferenceEquals(Instance, this))
            Instance = null;
        base.OnDestroy();
        Log.Info("FlyScreen", "cursor overlay service destroyed");
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void EnterForeground()
    {
        if (_foregrounded) return;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var manager = GetSystemService(NotificationService) as NotificationManager;
                manager?.CreateNotificationChannel(new NotificationChannel(
                    ChannelId, "远程指针", NotificationImportance.Low));
            }

            var notification = new Notification.Builder(this, ChannelId)
                .SetContentTitle("玄鸟")
                .SetContentText("远程指针显示中")
                .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
                .SetOngoing(true)
                .Build();

            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotifyId, notification, ForegroundService.TypeSpecialUse);
            else
                StartForeground(NotifyId, notification);

            _foregrounded = true;
        }
        catch (Exception ex)
        {
            lock (Gate)
                _lastError = ex.Message;
            Log.Warn("FlyScreen", "cursor FGS notify: " + ex.Message);
        }
    }

    private void ScheduleFlush()
    {
        lock (Gate)
        {
            if (_scheduled) return;
            _scheduled = true;
        }

        void Run()
        {
            lock (Gate)
                _scheduled = false;
            Flush();
        }

        if (_ui != null)
            _ui.Post(Run);
        else
            MainThread.BeginInvokeOnMainThread(Run);
    }

    private void Flush()
    {
        bool want;
        double nx, ny;
        lock (Gate)
        {
            want = _wantVisible;
            nx = _nx;
            ny = _ny;
        }

        if (!want)
        {
            Hide();
            return;
        }

        if (!FlyScreenCursorOverlay.CanDraw())
        {
            Hide();
            lock (Gate)
                _lastError = "无悬浮窗权限";
            return;
        }

        if (!EnsureView())
            return;

        RefreshScreen();
        PlaceWindow(nx, ny);

        try
        {
            if (!_added)
            {
                _wm!.AddView(_cursor, _lp);
                _added = true;
                lock (Gate)
                {
                    _failCount = 0;
                    _lastError = null;
                }
                Log.Info("FlyScreen", $"FGS cursor ON {_cursorPx}px screen={_screenW}x{_screenH}");
                AppLog.Info($"前台服务悬浮指针已开启 ({_screenW}x{_screenH})");
            }
            else
            {
                _wm!.UpdateViewLayout(_cursor, _lp);
            }

            _cursor!.Invalidate();
        }
        catch (Exception ex)
        {
            _added = false;
            lock (Gate)
            {
                _failCount++;
                _lastError = ex.Message;
                if (_failCount <= 3 || _failCount % 30 == 0)
                    AppLog.Warn($"前台服务悬浮指针失败({_failCount}): {ex.Message}");
            }
            Log.Warn("FlyScreen", "FGS cursor: " + ex.Message);
            ResetView();
        }
    }

    private void PlaceWindow(double nx, double ny)
    {
        var size = _cursorPx;
        var x = (int)Math.Round(nx * Math.Max(_screenW - 1, 1));
        var y = (int)Math.Round(ny * Math.Max(_screenH - 1, 1));
        _lp!.X = Math.Clamp(x, 0, Math.Max(_screenW - size, 0));
        _lp.Y = Math.Clamp(y, 0, Math.Max(_screenH - size, 0));
        _lp.Width = size;
        _lp.Height = size;
    }

    private void Hide()
    {
        if (!_added || _wm == null || _cursor == null) return;
        try { _wm.RemoveView(_cursor); } catch { /* ignore */ }
        _added = false;
    }

    private void ResetView()
    {
        Hide();
        _cursor = null;
        _lp = null;
        _wm = null;
        // 保留 _windowCtx，避免每次失败都重建
    }

    private static IWindowManager? ResolveWindowManager(Context ctx)
    {
        try
        {
            var raw = ctx.GetSystemService(Context.WindowService);
            if (raw == null) return null;
            return raw as IWindowManager ?? raw.JavaCast<IWindowManager>();
        }
        catch (Exception ex)
        {
            Log.Warn("FlyScreen", "cursor WM cast: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Service 没有关联 Display。先取默认屏再 createWindowContext，才能拿到可加悬浮窗的 WindowManager。
    /// </summary>
    private Context? TryOverlayContext(int overlayType)
    {
        if (_windowCtx != null) return _windowCtx;
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return this;

        try
        {
            var dmRaw = GetSystemService(Context.DisplayService);
            var dm = dmRaw as DisplayManager ?? dmRaw?.JavaCast<DisplayManager>();
            var display = dm?.GetDisplay(global::Android.Views.Display.DefaultDisplay);
            if (display == null)
            {
                var displays = dm?.GetDisplays();
                if (displays is { Length: > 0 })
                    display = displays[0];
            }

            if (display == null)
            {
                Log.Warn("FlyScreen", "cursor: Display 为空");
                return null;
            }

            var displayCtx = CreateDisplayContext(display);
            if (displayCtx == null)
            {
                Log.Warn("FlyScreen", "cursor: DisplayContext 为空");
                return null;
            }

            _windowCtx = displayCtx.CreateWindowContext(overlayType, null);
            return _windowCtx;
        }
        catch (Exception ex)
        {
            Log.Warn("FlyScreen", "cursor display context: " + ex.Message);
            lock (Gate)
                _lastError = "建窗口上下文失败: " + ex.Message;
            return null;
        }
    }

    private bool EnsureView()
    {
        if (_cursor != null && _wm != null && _lp != null) return true;

        // Android 11+：Service 本身没有 Display，不能直接 CreateWindowContext。
        // 必须先 CreateDisplayContext，再在该 Context 上 CreateWindowContext。
        var overlayType = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? WindowManagerTypes.ApplicationOverlay
            : WindowManagerTypes.SystemAlert;

        var viewCtx = TryOverlayContext((int)overlayType);
        if (viewCtx != null)
            _wm = ResolveWindowManager(viewCtx);

        if (_wm == null)
        {
            var act = Platform.CurrentActivity;
            if (act != null)
            {
                viewCtx = act;
                _wm = ResolveWindowManager(act);
            }
        }

        if (_wm == null || viewCtx == null)
        {
            lock (Gate)
                _lastError = "WindowManager 为空";
            return false;
        }

        var density = viewCtx.Resources?.DisplayMetrics?.Density
                      ?? Resources?.DisplayMetrics?.Density
                      ?? 2f;
        _cursorPx = Math.Max(56, (int)(CursorDp * density));
        _cursor = new PointerView(viewCtx, density);

        var type = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? WindowManagerTypes.ApplicationOverlay
            : WindowManagerTypes.SystemAlert;

        _lp = new WindowManagerLayoutParams(
            _cursorPx,
            _cursorPx,
            type,
            WindowManagerFlags.NotFocusable
            | WindowManagerFlags.NotTouchable
            | WindowManagerFlags.NotTouchModal
            | WindowManagerFlags.LayoutInScreen,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = 0,
            Y = 0,
            Alpha = 1f,
            Title = "FlyScreenCursor"
        };

        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            _lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

        return true;
    }

    private void RefreshScreen()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bounds = _wm?.CurrentWindowMetrics?.Bounds;
                if (bounds != null && bounds.Width() > 0 && bounds.Height() > 0)
                {
                    _screenW = bounds.Width();
                    _screenH = bounds.Height();
                    return;
                }
            }

            var size = new global::Android.Graphics.Point();
            _wm?.DefaultDisplay?.GetRealSize(size);
            if (size.X > 0 && size.Y > 0)
            {
                _screenW = size.X;
                _screenH = size.Y;
                return;
            }
        }
        catch
        {
            /* fall through */
        }

        var dm = Resources?.DisplayMetrics;
        if (dm == null) return;
        _screenW = dm.WidthPixels;
        _screenH = dm.HeightPixels;
    }

    private void StopSelfSafe()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
        }
        catch
        {
            /* ignore */
        }

        _foregrounded = false;
        StopSelf();
    }

    private sealed class PointerView : View
    {
        private readonly float _density;
        private readonly global::Android.Graphics.Paint _fill;
        private readonly global::Android.Graphics.Paint _stroke;
        private readonly global::Android.Graphics.Paint _halo;
        private readonly Path _arrow;

        public PointerView(Context context, float density) : base(context)
        {
            _density = density <= 0 ? 2f : density;
            SetWillNotDraw(false);
            SetLayerType(LayerType.Software, null);
            SetBackgroundColor(global::Android.Graphics.Color.Transparent);

            _fill = new global::Android.Graphics.Paint(PaintFlags.AntiAlias)
            {
                Color = global::Android.Graphics.Color.Rgb(255, 214, 10)
            };
            _fill.SetStyle(global::Android.Graphics.Paint.Style.Fill);

            _stroke = new global::Android.Graphics.Paint(PaintFlags.AntiAlias)
            {
                Color = global::Android.Graphics.Color.Black,
                StrokeWidth = 2f
            };
            _stroke.StrokeJoin = global::Android.Graphics.Paint.Join.Round;
            _stroke.SetStyle(global::Android.Graphics.Paint.Style.Stroke);

            _halo = new global::Android.Graphics.Paint(PaintFlags.AntiAlias)
            {
                Color = global::Android.Graphics.Color.Argb(120, 0, 0, 0)
            };
            _halo.SetStyle(global::Android.Graphics.Paint.Style.Fill);

            _arrow = new Path();
            _arrow.MoveTo(1, 1);
            _arrow.LineTo(1, 26);
            _arrow.LineTo(7, 20);
            _arrow.LineTo(12, 30);
            _arrow.LineTo(16, 28);
            _arrow.LineTo(11, 18);
            _arrow.LineTo(18, 18);
            _arrow.Close();
        }

        protected override void OnDraw(Canvas canvas)
        {
            var scale = Math.Max(Width, Height) / 32f;
            if (scale < 0.5f) scale = _density;
            canvas.Save();
            canvas.Scale(scale, scale);
            canvas.Translate(0.6f, 0.6f);
            canvas.DrawPath(_arrow, _halo);
            canvas.Translate(-0.6f, -0.6f);
            canvas.DrawPath(_arrow, _fill);
            canvas.DrawPath(_arrow, _stroke);
            canvas.Restore();
        }
    }
}
#endif
