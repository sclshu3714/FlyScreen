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
/// 无障碍服务：接收桌面 EasyMouse / 键鼠注入（点击、滑动、光标叠加层）。
/// 用户须在系统设置中手动开启「玄鸟键鼠注入」。
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

    private IWindowManager? _wm;
    private View? _cursorView;
    private WindowManagerLayoutParams? _cursorLp;
    private float _nx = 0.5f;
    private float _ny = 0.5f;
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
        RefreshScreenSize();
        _nx = (float)Math.Clamp(entryX, 0, 1);
        _ny = (float)Math.Clamp(entryY, 0, 1);
        ShowCursor();
        MoveCursorOverlay();
    }

    public void EndSession()
    {
        HideCursor();
    }

    public void MoveAbsolute(double nx, double ny)
    {
        _nx = (float)Math.Clamp(nx, 0, 1);
        _ny = (float)Math.Clamp(ny, 0, 1);
        ShowCursor();
        MoveCursorOverlay();
    }

    public void MoveRelative(int dx, int dy)
    {
        if (_screenW <= 0 || _screenH <= 0) RefreshScreenSize();
        _nx = Math.Clamp(_nx + dx / (float)_screenW, 0f, 1f);
        _ny = Math.Clamp(_ny + dy / (float)_screenH, 0f, 1f);
        ShowCursor();
        MoveCursorOverlay();
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
        => (_nx * Math.Max(_screenW - 1, 1), _ny * Math.Max(_screenH - 1, 1));

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
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable | WindowManagerFlags.LayoutNoLimits,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = 0,
            Y = 0
        };
    }

    private void ShowCursor()
    {
        if (_cursorVisible) return;
        EnsureCursorOverlay();
        if (_wm == null || _cursorView == null || _cursorLp == null) return;
        try
        {
            _wm.AddView(_cursorView, _cursorLp);
            _cursorVisible = true;
        }
        catch { /* already added */ }
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

    private void MoveCursorOverlay()
    {
        if (_cursorLp == null || _cursorView == null || !_cursorVisible) return;
        var (x, y) = ToPixels();
        var half = _cursorLp.Width / 2;
        _cursorLp.X = (int)x - half;
        _cursorLp.Y = (int)y - half;
        try { _wm?.UpdateViewLayout(_cursorView, _cursorLp); } catch { /* ignore */ }
    }
}
#endif
