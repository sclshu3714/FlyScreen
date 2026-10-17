#if ANDROID
using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Accessibility;
using AndroidX.Core.View;
using Path = Android.Graphics.Path;
using View = Android.Views.View;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 系统手势接口，并在服务已开启时用无障碍叠层画指针。
/// 叠层窗口必须在主线程添加；悬浮窗在小米后台经常加不上。
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
    private IWindowManager? _wm;
    private View? _cursorView;
    private WindowManagerLayoutParams? _cursorLp;
    private float _nx = 0.5f;
    private float _ny = 0.5f;
    private bool _cursorWanted;
    private bool _cursorScheduled;
    private bool _cursorVisible;
    private int _screenW = 1080;
    private int _screenH = 1920;

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
        var size = (int)(18 * density);
        _cursorView = new View(this);
        _cursorView.SetBackgroundColor(global::Android.Graphics.Color.Argb(220, 37, 99, 235));
        ViewCompat.SetElevation(_cursorView, 24f);

        var type = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? WindowManagerTypes.AccessibilityOverlay
            : WindowManagerTypes.SystemAlert;

        _cursorLp = new WindowManagerLayoutParams(
            size, size, type,
            WindowManagerFlags.NotFocusable
            | WindowManagerFlags.NotTouchable
            | WindowManagerFlags.LayoutNoLimits
            | WindowManagerFlags.LayoutInScreen,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = 0,
            Y = 0
        };
    }

    private void ScheduleCursor()
    {
        lock (_cursorGate)
        {
            if (_cursorScheduled) return;
            _cursorScheduled = true;
        }
        _ui.Post(FlushCursor);
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
        var x = (int)(nx * Math.Max(_screenW - 1, 1));
        var y = (int)(ny * Math.Max(_screenH - 1, 1));
        _cursorLp.X = x - _cursorLp.Width / 2;
        _cursorLp.Y = y - _cursorLp.Height / 2;
        try
        {
            if (!_cursorVisible)
            {
                _wm.AddView(_cursorView, _cursorLp);
                _cursorVisible = true;
            }
            else
            {
                _wm.UpdateViewLayout(_cursorView, _cursorLp);
            }
        }
        catch
        {
            _cursorVisible = false;
        }
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

}
#endif
