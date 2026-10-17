#if ANDROID
using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Util;
using Android.Views;
using Android.Views.Accessibility;
using Android.Widget;
using Path = Android.Graphics.Path;
using View = Android.Views.View;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 系统手势接口，并在服务已开启时画指针。
/// 小米会裁掉带 LayoutNoLimits 的小块无障碍窗，所以用全屏透明层自己画箭头。
/// </summary>
[Service(
    Label = "@string/flyscreen_accessibility_label",
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
    Exported = false)]
[IntentFilter(["android.accessibilityservice.AccessibilityService"])]
[MetaData("android.accessibilityservice", Resource = "@xml/flyscreen_accessibility_config")]
public sealed class FlyScreenInputAccessibilityService : AccessibilityService
{
    public static FlyScreenInputAccessibilityService? Instance { get; private set; }

    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly object _cursorGate = new();
    private FlushRunnable? _flush;
    private IWindowManager? _wm;
    private PointerView? _cursorView;
    private WindowManagerLayoutParams? _cursorLp;
    private float _nx = 0.5f;
    private float _ny = 0.5f;
    private bool _cursorWanted;
    private bool _cursorScheduled;
    private bool _cursorVisible;
    private bool _reportedAddFailure;
    private int _screenW = 1080;
    private int _screenH = 1920;

    /// <summary>全屏指针层已经加到 WindowManager。加不上时为 false，调用方可改走悬浮窗。</summary>
    public bool IsCursorVisible => _cursorVisible;

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Instance = this;
        RefreshScreenSize();
        EnsureCursorOverlay();
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e) { }

    public override void OnInterrupt() { }

    public override void OnDestroy()
    {
        RemoveCursorOverlay();
        if (ReferenceEquals(Instance, this))
            Instance = null;
        base.OnDestroy();
    }

    public static bool IsEnabled => Instance != null;

    public static void OpenAccessibilitySettings()
    {
        var ctx = global::Android.App.Application.Context;
        var intent = new Intent(global::Android.Provider.Settings.ActionAccessibilitySettings);
        intent.AddFlags(ActivityFlags.NewTask);
        ctx.StartActivity(intent);
    }

    public void BeginSession(double entryX, double entryY)
    {
        lock (_cursorGate)
        {
            _nx = (float)Math.Clamp(entryX, 0, 1);
            _ny = (float)Math.Clamp(entryY, 0, 1);
            _cursorWanted = true;
        }
        ScheduleCursor();
    }

    public void EndSession()
    {
        lock (_cursorGate)
            _cursorWanted = false;
        ScheduleCursor();
    }

    public void MoveAbsolute(double nx, double ny)
    {
        lock (_cursorGate)
        {
            _nx = (float)Math.Clamp(nx, 0, 1);
            _ny = (float)Math.Clamp(ny, 0, 1);
            _cursorWanted = true;
        }
        ScheduleCursor();
    }

    public void MoveRelative(int dx, int dy)
    {
        lock (_cursorGate)
        {
            var w = Math.Max(_screenW, 1);
            var h = Math.Max(_screenH, 1);
            _nx = Math.Clamp(_nx + dx / (float)w, 0f, 1f);
            _ny = Math.Clamp(_ny + dy / (float)h, 0f, 1f);
            _cursorWanted = true;
        }
        ScheduleCursor();
    }

    public void Click(int button, bool down)
    {
        // Android 无障碍手势多为完整 tap；在 Down 时派发一次点击（右键降级为长按）
        if (!down) return;
        var (x, y) = ToPixels();
        if (button == 1)
            DispatchLongPress(x, y);
        else
            DispatchTap(x, y);
    }

    public void Wheel(int delta, bool horizontal)
    {
        var (x, y) = ToPixels();
        var dist = Math.Clamp(Math.Abs(delta) / 3f, 40f, 280f);
        float x2 = x, y2 = y;
        if (horizontal)
            x2 = delta > 0 ? x - dist : x + dist;
        else
            y2 = delta > 0 ? y - dist : y + dist;
        DispatchSwipe(x, y, x2, y2, 120);
    }

    public void Key(ushort vk, bool down)
    {
        if (!down) return;
        switch (vk)
        {
            case 0x08: // Backspace → Back（无直接删字权限时的降级）
            case 0x1B: // Esc
                PerformGlobalAction(global::Android.AccessibilityServices.GlobalAction.Back);
                break;
            case 0x5B: // LWin / Home
                PerformGlobalAction(global::Android.AccessibilityServices.GlobalAction.Home);
                break;
            case 0x0D: // Enter — 尝试对焦点节点确认
                TryActionOnFocus(global::Android.Views.Accessibility.Action.Click);
                break;
            default:
                TryShellKey(VkToAndroidKeyCode(vk));
                break;
        }
    }

    private void TryActionOnFocus(global::Android.Views.Accessibility.Action action)
    {
        try
        {
            var root = RootInActiveWindow;
            var focus = root?.FindFocus(NodeFocus.Input) ?? root?.FindFocus(NodeFocus.Accessibility);
            focus?.PerformAction(action);
        }
        catch { /* ignore */ }
    }

    private static void TryShellKey(int keyCode)
    {
        if (keyCode <= 0) return;
        try
        {
            Java.Lang.Runtime.GetRuntime()!.Exec(["input", "keyevent", keyCode.ToString()]);
        }
        catch { /* 多数机型无 INJECT_EVENTS，失败则忽略 */ }
    }

    private static int VkToAndroidKeyCode(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A => 29 + (vk - 0x41), // KEYCODE_A=29
        >= 0x30 and <= 0x39 => 7 + (vk - 0x30),  // KEYCODE_0=7
        0x20 => 62, // SPACE
        0x09 => 61, // TAB
        0x25 => 21, // LEFT
        0x26 => 19, // UP
        0x27 => 22, // RIGHT
        0x28 => 20, // DOWN
        0x2E => 112, // DELETE
        _ => -1
    };

    private (float x, float y) ToPixels()
    {
        lock (_cursorGate)
            return (_nx * Math.Max(_screenW - 1, 1), _ny * Math.Max(_screenH - 1, 1));
    }

    private void RefreshScreenSize()
    {
        var dm = Resources?.DisplayMetrics;
        if (dm == null) return;
        _screenW = dm.WidthPixels;
        _screenH = dm.HeightPixels;
    }

    private void DispatchTap(float x, float y)
    {
        var path = new Path();
        path.MoveTo(x, y);
        var stroke = new GestureDescription.StrokeDescription(path, 0, 50);
        var builder = new GestureDescription.Builder();
        builder.AddStroke(stroke);
        var gesture = builder.Build();
        if (gesture != null)
            DispatchGesture(gesture, null, null);
    }

    private void DispatchLongPress(float x, float y)
    {
        var path = new Path();
        path.MoveTo(x, y);
        var stroke = new GestureDescription.StrokeDescription(path, 0, 700);
        var builder = new GestureDescription.Builder();
        builder.AddStroke(stroke);
        var gesture = builder.Build();
        if (gesture != null)
            DispatchGesture(gesture, null, null);
    }

    private void DispatchSwipe(float x1, float y1, float x2, float y2, int durationMs)
    {
        var path = new Path();
        path.MoveTo(x1, y1);
        path.LineTo(x2, y2);
        var stroke = new GestureDescription.StrokeDescription(path, 0, durationMs);
        var builder = new GestureDescription.Builder();
        builder.AddStroke(stroke);
        var gesture = builder.Build();
        if (gesture != null)
            DispatchGesture(gesture, null, null);
    }

    private void EnsureCursorOverlay()
    {
        if (_cursorView != null) return;
        _wm = GetSystemService(WindowService) as IWindowManager;
        if (_wm == null) return;

        var density = Resources?.DisplayMetrics?.Density ?? 2f;
        _cursorView = new PointerView(this, density);
        _cursorLp = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent,
            WindowManagerTypes.AccessibilityOverlay,
            WindowManagerFlags.NotFocusable
            | WindowManagerFlags.NotTouchable
            | WindowManagerFlags.NotTouchModal
            | WindowManagerFlags.LayoutInScreen,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = 0,
            Y = 0,
            Alpha = 1f
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            _cursorLp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
    }

    private void ScheduleCursor()
    {
        if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper)
        {
            FlushCursor();
            return;
        }

        lock (_cursorGate)
        {
            if (_cursorScheduled) return;
            _cursorScheduled = true;
        }
        _flush ??= new FlushRunnable(this);
        _ui.Post(_flush);
    }

    private void FlushCursor()
    {
        bool want;
        float nx, ny;
        lock (_cursorGate)
        {
            want = _cursorWanted;
            nx = _nx;
            ny = _ny;
            _cursorScheduled = false;
        }

        if (!want)
        {
            HideCursor();
            return;
        }

        RefreshScreenSize();
        EnsureCursorOverlay();
        if (_wm == null || _cursorView == null || _cursorLp == null) return;
        _cursorView.SetNormalized(nx, ny);
        if (!_cursorVisible && !TryAddCursor())
        {
            ReportAddFailure();
            return;
        }

        _cursorVisible = true;
        _cursorView.Invalidate();
    }

    private bool TryAddCursor()
    {
        if (_wm == null || _cursorView == null || _cursorLp == null) return false;
        var types = new List<WindowManagerTypes> { WindowManagerTypes.AccessibilityOverlay, WindowManagerTypes.SystemAlert };
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            types.Insert(1, WindowManagerTypes.ApplicationOverlay);
        foreach (var type in types)
        {
            if (type == WindowManagerTypes.ApplicationOverlay && !Settings.CanDrawOverlays(this))
                continue;
            _cursorLp.Type = type;
            try
            {
                _wm.AddView(_cursorView, _cursorLp);
                Log.Info("FlyScreen", $"cursor window added type={type}");
                return true;
            }
            catch (System.Exception ex)
            {
                Log.Warn("FlyScreen", $"cursor add {type} failed: {ex.Message}");
                try { _wm.RemoveView(_cursorView); } catch { /* 未加上 */ }
            }
        }
        return false;
    }

    private void ReportAddFailure()
    {
        if (_reportedAddFailure) return;
        _reportedAddFailure = true;
        Toast.MakeText(this, "看不到指针时，请允许玄鸟「显示在其他应用上层」和「后台弹出界面」", ToastLength.Long)?.Show();
    }

    private void HideCursor()
    {
        if (!_cursorVisible) return;
        try { _wm?.RemoveView(_cursorView); } catch { /* ignore */ }
        _cursorVisible = false;
    }

    private void RemoveCursorOverlay()
    {
        HideCursor();
        _cursorView = null;
        _cursorLp = null;
    }

    private sealed class FlushRunnable : Java.Lang.Object, Java.Lang.IRunnable
    {
        private readonly FlyScreenInputAccessibilityService _service;
        public FlushRunnable(FlyScreenInputAccessibilityService service) => _service = service;
        public void Run() => _service.FlushCursor();
    }

    /// <summary>盖住全屏但不吃触摸，只在归一化坐标处画箭头。避免小窗在 MIUI 上被裁成 0。</summary>
    private sealed class PointerView : View
    {
        private readonly float _density;
        private readonly global::Android.Graphics.Paint _fill;
        private readonly global::Android.Graphics.Paint _stroke;
        private readonly Path _arrow;
        private float _nx = 0.5f;
        private float _ny = 0.5f;

        public PointerView(Context context, float density) : base(context)
        {
            _density = density <= 0 ? 2f : density;
            SetWillNotDraw(false);
            SetBackgroundColor(global::Android.Graphics.Color.Transparent);
            _fill = new global::Android.Graphics.Paint(PaintFlags.AntiAlias);
            _fill.Color = global::Android.Graphics.Color.Rgb(37, 99, 235);
            _fill.SetStyle(global::Android.Graphics.Paint.Style.Fill);
            _stroke = new global::Android.Graphics.Paint(PaintFlags.AntiAlias);
            _stroke.Color = global::Android.Graphics.Color.White;
            _stroke.StrokeWidth = 2.5f;
            _stroke.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
            _arrow = new Path();
            _arrow.MoveTo(0, 0);
            _arrow.LineTo(0, 22);
            _arrow.LineTo(6, 17);
            _arrow.LineTo(11, 26);
            _arrow.LineTo(15, 24);
            _arrow.LineTo(10, 15);
            _arrow.LineTo(17, 15);
            _arrow.Close();
        }

        public void SetNormalized(float nx, float ny)
        {
            _nx = nx;
            _ny = ny;
        }

        protected override void OnDraw(Canvas canvas)
        {
            var w = Width;
            var h = Height;
            if (w <= 1 || h <= 1)
            {
                var dm = Resources?.DisplayMetrics;
                w = dm?.WidthPixels ?? 0;
                h = dm?.HeightPixels ?? 0;
            }
            if (w <= 1 || h <= 1) return;
            canvas.Save();
            canvas.Translate(_nx * (w - 1), _ny * (h - 1));
            canvas.Scale(_density, _density);
            canvas.DrawPath(_arrow, _fill);
            canvas.DrawPath(_arrow, _stroke);
            canvas.Restore();
        }
    }

}
#endif
