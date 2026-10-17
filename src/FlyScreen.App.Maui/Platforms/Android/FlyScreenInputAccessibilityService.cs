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
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSpecialUse)]
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
    private WindowManagerTypes _overlayType = WindowManagerTypes.AccessibilityOverlay;
    private FlyScreenImeProxy? _ime;
    private global::Android.Views.Accessibility.AccessibilityNodeInfo? _imeTarget;
    private float _nx = 0.5f;
    private float _ny = 0.5f;
    private bool _cursorWanted;
    private bool _cursorScheduled;
    private bool _cursorVisible;
    private bool _reportedAddFailure;
    private bool _shift;
    private bool _ctrl;
    private bool _caps;
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

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (e == null || !_cursorWanted) return;
        var type = e.EventType;
        if (type != global::Android.Views.Accessibility.EventTypes.ViewFocused
            && type != global::Android.Views.Accessibility.EventTypes.ViewClicked
            && type != global::Android.Views.Accessibility.EventTypes.WindowStateChanged)
            return;
        if (string.Equals(e.PackageName?.ToString(), PackageName, StringComparison.Ordinal))
            return;
        RememberExternalTarget();
    }

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
        _ui.Post(ReleaseRemoteSession);
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
        _ui.PostDelayed(RememberExternalTarget, 320);
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
        _ui.Post(() => ApplyKey(vk, down));
    }

    private void ApplyKey(ushort vk, bool down)
    {
        switch (vk)
        {
            case 0x10 or 0xA0 or 0xA1:
                _shift = down;
                _ime?.Dispatch((int)global::Android.Views.Keycode.ShiftLeft, down, false);
                return;
            case 0x11 or 0xA2 or 0xA3:
                _ctrl = down;
                return;
            case 0x14:
                if (down) _caps = !_caps;
                return;
            case 0x12 or 0xA4 or 0xA5:
                return;
        }

        var keyCode = VkToAndroidKeyCode(vk);
        if (!down)
        {
            if (keyCode > 0)
                _ime?.Dispatch(keyCode, false, false);
            return;
        }

        if (vk == 0x1B)
        {
            PerformGlobalAction(global::Android.AccessibilityServices.GlobalAction.Back);
            return;
        }
        if (vk == 0x5B || vk == 0x5C)
        {
            PerformGlobalAction(global::Android.AccessibilityServices.GlobalAction.Home);
            return;
        }

        if (TryThroughPhoneIme(vk, keyCode))
            return;

        var node = CurrentTarget();
        if (node != null && EditText(node, vk))
            return;

        if (vk == 0x08)
            PerformGlobalAction(global::Android.AccessibilityServices.GlobalAction.Back);
    }

    private bool EditText(global::Android.Views.Accessibility.AccessibilityNodeInfo node, ushort vk)
    {
        try
        {
            node.Refresh();
            if (_ctrl && vk == 0x41)
                return SelectRange(node, 0, CurrentText(node).Length);
            if (_ctrl && vk == 0x43)
                return node.PerformAction(global::Android.Views.Accessibility.Action.Copy);
            if (_ctrl && vk == 0x58)
                return node.PerformAction(global::Android.Views.Accessibility.Action.Cut);
            if (_ctrl && vk == 0x56)
                return InsertText(node, ReadClipboard());
            if (vk == 0x09)
                return MoveFocus(node);
            if (vk == 0x08)
                return DeleteBackward(node);
            if (vk == 0x2E)
                return DeleteForward(node);
            if (vk == 0x0D)
                return SubmitOrNewline(node);
            if (vk is 0x25 or 0x26 or 0x27 or 0x28)
                return MoveCaret(node, vk);
            if (_ctrl)
                return false;

            var upper = vk is >= 0x41 and <= 0x5A ? (_shift ^ _caps) : _shift;
            var text = VkToChar(vk, upper);
            if (string.IsNullOrEmpty(text)) return false;
            return InsertText(node, text);
        }
        catch
        {
            return false;
        }
    }

    private static string CurrentText(global::Android.Views.Accessibility.AccessibilityNodeInfo node)
        => node.Text?.ToString() ?? "";

    private static (int start, int end) Selection(global::Android.Views.Accessibility.AccessibilityNodeInfo node, string text)
    {
        var start = node.TextSelectionStart;
        var end = node.TextSelectionEnd;
        if (start < 0 || end < 0)
            start = end = text.Length;
        if (start > end)
            (start, end) = (end, start);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);
        return (start, end);
    }

    private bool InsertText(global::Android.Views.Accessibility.AccessibilityNodeInfo node, string? insert)
    {
        if (string.IsNullOrEmpty(insert)) return false;
        var text = CurrentText(node);
        var (start, end) = Selection(node, text);
        var next = text.Remove(start, end - start).Insert(start, insert);
        if (!SetText(node, next)) return false;
        var caret = start + insert.Length;
        SelectRange(node, caret, caret);
        return true;
    }

    private bool DeleteBackward(global::Android.Views.Accessibility.AccessibilityNodeInfo node)
    {
        var text = CurrentText(node);
        var (start, end) = Selection(node, text);
        if (start != end)
            return Replace(node, text, start, end, "");
        if (start == 0) return true;
        var from = start - 1;
        if (from > 0 && char.IsLowSurrogate(text[start - 1]) && char.IsHighSurrogate(text[from - 1]))
            from--;
        return Replace(node, text, from, start, "");
    }

    private bool DeleteForward(global::Android.Views.Accessibility.AccessibilityNodeInfo node)
    {
        var text = CurrentText(node);
        var (start, end) = Selection(node, text);
        if (start != end)
            return Replace(node, text, start, end, "");
        if (start >= text.Length) return true;
        var to = start + 1;
        if (to < text.Length && char.IsHighSurrogate(text[start]) && char.IsLowSurrogate(text[to]))
            to++;
        return Replace(node, text, start, to, "");
    }

    private bool Replace(global::Android.Views.Accessibility.AccessibilityNodeInfo node, string text, int start, int end, string insert)
    {
        var next = text.Remove(start, end - start).Insert(start, insert);
        if (!SetText(node, next)) return false;
        var caret = start + insert.Length;
        SelectRange(node, caret, caret);
        return true;
    }

    private bool MoveCaret(global::Android.Views.Accessibility.AccessibilityNodeInfo node, ushort vk)
    {
        var text = CurrentText(node);
        var (start, end) = Selection(node, text);
        var caret = vk is 0x25 or 0x26 ? start : end;
        if (_shift)
        {
            caret = vk switch
            {
                0x25 => Math.Max(0, start - 1),
                0x27 => Math.Min(text.Length, end + 1),
                0x26 => PreviousLine(text, start),
                _ => NextLine(text, end)
            };
            return vk is 0x25 or 0x26
                ? SelectRange(node, caret, end)
                : SelectRange(node, start, caret);
        }

        if (start != end && vk is 0x25 or 0x27)
            return SelectRange(node, vk == 0x25 ? start : end, vk == 0x25 ? start : end);

        caret = vk switch
        {
            0x25 => Math.Max(0, caret - 1),
            0x27 => Math.Min(text.Length, caret + 1),
            0x26 => PreviousLine(text, caret),
            _ => NextLine(text, caret)
        };
        return SelectRange(node, caret, caret);
    }

    private static int PreviousLine(string text, int caret)
    {
        var line = text.LastIndexOf('\n', Math.Max(0, caret - 1));
        if (line < 0) return 0;
        var prev = text.LastIndexOf('\n', Math.Max(0, line - 1));
        var col = caret - line - 1;
        var start = prev + 1;
        return Math.Min(start + col, line);
    }

    private static int NextLine(string text, int caret)
    {
        var line = text.LastIndexOf('\n', Math.Max(0, caret - 1));
        var next = text.IndexOf('\n', caret);
        if (next < 0) return text.Length;
        var col = caret - (line < 0 ? 0 : line + 1);
        var start = next + 1;
        var end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;
        return Math.Min(start + col, end);
    }

    private bool SubmitOrNewline(global::Android.Views.Accessibility.AccessibilityNodeInfo node)
    {
        // AccessibilityNodeInfo.ACTION_IME_ENTER
        if (OperatingSystem.IsAndroidVersionAtLeast(30)
            && node.PerformAction((global::Android.Views.Accessibility.Action)0x00200000))
            return true;
        return InsertText(node, "\n");
    }

    private static bool MoveFocus(global::Android.Views.Accessibility.AccessibilityNodeInfo node)
    {
        var next = node.FocusSearch(global::Android.Views.FocusSearchDirection.Forward);
        if (next == null) return false;
        try
        {
            return next.PerformAction(global::Android.Views.Accessibility.Action.Focus)
                   || next.PerformAction(global::Android.Views.Accessibility.Action.AccessibilityFocus);
        }
        finally
        {
            if (!ReferenceEquals(next, node))
                next.Dispose();
        }
    }

    private string? ReadClipboard()
    {
        try
        {
            var clipboard = GetSystemService(ClipboardService) as global::Android.Content.ClipboardManager;
            var clip = clipboard?.PrimaryClip;
            if (clip == null || clip.ItemCount == 0) return null;
            return clip.GetItemAt(0)?.Text?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool SetText(global::Android.Views.Accessibility.AccessibilityNodeInfo node, string text)
    {
        var bundle = new Bundle();
        bundle.PutString(global::Android.Views.Accessibility.AccessibilityNodeInfo.ActionArgumentSetTextCharsequence, text);
        return node.PerformAction(global::Android.Views.Accessibility.Action.SetText, bundle);
    }

    private static bool SelectRange(global::Android.Views.Accessibility.AccessibilityNodeInfo node, int start, int end)
    {
        var bundle = new Bundle();
        bundle.PutInt(global::Android.Views.Accessibility.AccessibilityNodeInfo.ActionArgumentSelectionStartInt, start);
        bundle.PutInt(global::Android.Views.Accessibility.AccessibilityNodeInfo.ActionArgumentSelectionEndInt, end);
        return node.PerformAction(global::Android.Views.Accessibility.Action.SetSelection, bundle);
    }

    private bool TryThroughPhoneIme(ushort vk, int keyCode)
    {
        if (keyCode <= 0 || _ctrl) return false;
        if (_ime?.HasContent != true && vk is 0x08 or 0x2E or 0x0D or 0x25 or 0x26 or 0x27 or 0x28)
            return false;
        if (!EnsureIme()) return false;
        var shift = vk is >= 0x41 and <= 0x5A ? (_shift ^ _caps) : _shift;
        return _ime!.Dispatch(keyCode, true, shift);
    }

    private bool EnsureIme()
    {
        EnsureForeground();
        EnsureCursorOverlay();
        RefreshScreenSize();
        if (_wm == null) return false;
        if (!_cursorVisible && TryAddCursor())
            _cursorVisible = true;
        _ime ??= new FlyScreenImeProxy(this, OnImeCommitted, OnImeDeleted);
        return _ime.Ensure(_wm, _overlayType);
    }

    private void OnImeCommitted(string text)
    {
        var node = CurrentTarget();
        if (node == null || text.Length == 0) return;
        if (text == "\n")
            SubmitOrNewline(node);
        else
            InsertText(node, text);
    }

    private void OnImeDeleted(int utf16Count)
    {
        var node = CurrentTarget();
        if (node == null || utf16Count <= 0) return;
        try
        {
            node.Refresh();
            var text = CurrentText(node);
            var (start, end) = Selection(node, text);
            if (start != end)
            {
                Replace(node, text, start, end, "");
                return;
            }
            Replace(node, text, Math.Max(0, start - utf16Count), start, "");
        }
        catch
        {
            /* 目标输入框已失效 */
        }
    }

    private void RememberExternalTarget()
    {
        var found = FindEditable();
        if (found == null) return;
        if (!ReferenceEquals(found, _imeTarget))
        {
            _imeTarget?.Dispose();
            _imeTarget = found;
        }
    }

    private global::Android.Views.Accessibility.AccessibilityNodeInfo? CurrentTarget()
    {
        if (_imeTarget != null)
        {
            try
            {
                if (_imeTarget.Refresh()
                    && !string.Equals(_imeTarget.PackageName?.ToString(), PackageName, StringComparison.Ordinal))
                    return _imeTarget;
            }
            catch
            {
                /* 节点已失效 */
            }
            _imeTarget.Dispose();
            _imeTarget = null;
        }
        RememberExternalTarget();
        return _imeTarget;
    }

    private void ReleaseRemoteSession()
    {
        _ime?.Detach();
        _ime = null;
        _imeTarget?.Dispose();
        _imeTarget = null;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
        }
        catch
        {
            /* 通知渠道还没建时忽略 */
        }
    }

    private void EnsureForeground()
    {
        try
        {
            const string channelId = "flyscreen.remote";
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var manager = GetSystemService(NotificationService) as NotificationManager;
                manager?.CreateNotificationChannel(new NotificationChannel(channelId, "键鼠", NotificationImportance.Min));
            }
            var notification = new Notification.Builder(this, channelId)
                .SetContentTitle("玄鸟")
                .SetContentText("正在接收电脑键鼠")
                .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
                .SetOngoing(true)
                .Build();
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(41, notification, global::Android.Content.PM.ForegroundService.TypeSpecialUse);
            else
                StartForeground(41, notification);
        }
        catch (System.Exception ex)
        {
            Log.Warn("FlyScreen", "foreground " + ex.Message);
        }
    }

    private static int VkToAndroidKeyCode(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A => 29 + (vk - 0x41),
        >= 0x30 and <= 0x39 => 7 + (vk - 0x30),
        >= 0x60 and <= 0x69 => 7 + (vk - 0x60),
        0x20 => 62,
        0x08 => 67,
        0x2E => 112,
        0x0D => 66,
        0x09 => 61,
        0x25 => 21,
        0x26 => 19,
        0x27 => 22,
        0x28 => 20,
        0x6A => 17,
        0x6B => 81,
        0x6D => 69,
        0x6E => 56,
        0x6F => 76,
        0xBA => 74,
        0xBB => 70,
        0xBC => 55,
        0xBD => 69,
        0xBE => 56,
        0xBF => 76,
        0xC0 => 68,
        0xDB => 71,
        0xDC => 73,
        0xDD => 72,
        0xDE => 75,
        _ => -1
    };

    private global::Android.Views.Accessibility.AccessibilityNodeInfo? FindEditable()
    {
        try
        {
            var windows = Windows;
            if (windows != null)
            {
                foreach (var window in windows)
                {
                    var found = TakeEditable(window?.Root);
                    if (found != null) return found;
                }
            }
        }
        catch
        {
            /* 部分系统读窗口列表会抛 */
        }
        return TakeEditable(RootInActiveWindow);
    }

    private global::Android.Views.Accessibility.AccessibilityNodeInfo? TakeEditable(global::Android.Views.Accessibility.AccessibilityNodeInfo? root)
    {
        if (root == null) return null;
        global::Android.Views.Accessibility.AccessibilityNodeInfo? focus = null;
        try
        {
            focus = root.FindFocus(global::Android.Views.Accessibility.NodeFocus.Input);
        }
        catch
        {
            root.Dispose();
            return null;
        }

        if (focus != null && IsEditable(focus)
            && !string.Equals(focus.PackageName?.ToString(), PackageName, StringComparison.Ordinal))
        {
            if (!ReferenceEquals(focus, root))
                root.Dispose();
            return focus;
        }

        focus?.Dispose();
        root.Dispose();
        return null;
    }

    private static bool IsEditable(global::Android.Views.Accessibility.AccessibilityNodeInfo node)
    {
        if (node.Editable) return true;
        var className = node.ClassName?.ToString() ?? "";
        return className.Contains("EditText", StringComparison.Ordinal)
               || className.Contains("AutoCompleteTextView", StringComparison.Ordinal)
               || className.Contains("TextInput", StringComparison.Ordinal);
    }

    private static string? VkToChar(ushort vk, bool shift)
    {
        if (vk is >= 0x41 and <= 0x5A)
            return ((char)(shift ? vk : vk + 32)).ToString();
        if (vk is >= 0x30 and <= 0x39)
        {
            if (!shift) return ((char)vk).ToString();
            return (vk - 0x30) switch
            {
                0 => ")",
                1 => "!",
                2 => "@",
                3 => "#",
                4 => "$",
                5 => "%",
                6 => "^",
                7 => "&",
                8 => "*",
                9 => "(",
                _ => null
            };
        }
        if (vk is >= 0x60 and <= 0x69)
            return ((char)('0' + (vk - 0x60))).ToString();
        return vk switch
        {
            0x20 => " ",
            0x6A => "*",
            0x6B => "+",
            0x6D => "-",
            0x6E => ".",
            0x6F => "/",
            0xBA => shift ? ":" : ";",
            0xBB => shift ? "+" : "=",
            0xBC => shift ? "<" : ",",
            0xBD => shift ? "_" : "-",
            0xBE => shift ? ">" : ".",
            0xBF => shift ? "?" : "/",
            0xC0 => shift ? "~" : "`",
            0xDB => shift ? "{" : "[",
            0xDC => shift ? "|" : "\\",
            0xDD => shift ? "}" : "]",
            0xDE => shift ? "\"" : "'",
            _ => null
        };
    }

    private (float x, float y) ToPixels()
    {
        lock (_cursorGate)
            return (_nx * Math.Max(_screenW - 1, 1), _ny * Math.Max(_screenH - 1, 1));
    }

    private void RefreshScreenSize()
    {
        try
        {
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
            /* 旧接口失败时用 DisplayMetrics */
        }

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

        EnsureForeground();
        EnsureCursorOverlay();
        RefreshScreenSize();
        if (_wm == null || _cursorView == null || _cursorLp == null) return;
        _cursorLp.X = 0;
        _cursorLp.Y = 0;
        _cursorLp.Width = Math.Max(_screenW, 1);
        _cursorLp.Height = Math.Max(_screenH, 1);
        _cursorView.SetPoint(nx, ny, _screenW, _screenH);
        if (!_cursorVisible)
        {
            if (!TryAddCursor())
            {
                ReportAddFailure();
                return;
            }
            _cursorVisible = true;
        }
        else
        {
            try { _wm.UpdateViewLayout(_cursorView, _cursorLp); }
            catch
            {
                _cursorVisible = false;
                return;
            }
        }

        _cursorView.Invalidate();
    }

    private bool TryAddCursor()
    {
        if (_wm == null || _cursorView == null || _cursorLp == null) return false;
        var types = new List<WindowManagerTypes> { WindowManagerTypes.AccessibilityOverlay, WindowManagerTypes.SystemAlert };
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && Settings.CanDrawOverlays(this))
            types.Insert(0, WindowManagerTypes.ApplicationOverlay);
        foreach (var type in types)
        {
            if (type == WindowManagerTypes.ApplicationOverlay && !Settings.CanDrawOverlays(this))
                continue;
            _cursorLp.Type = type;
            try
            {
                _wm.AddView(_cursorView, _cursorLp);
                _overlayType = type;
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

    /// <summary>箭头尖在窗口左上角。窗口跟着坐标移动，不铺满全屏，玄鸟退到后台后仍然盖在其他应用上。</summary>
    private sealed class PointerView : View
    {
        private readonly float _density;
        private readonly global::Android.Graphics.Paint _fill;
        private readonly global::Android.Graphics.Paint _stroke;
        private readonly Path _arrow;

        private float _nx = 0.5f;
        private float _ny = 0.5f;
        private int _screenW = 1080;
        private int _screenH = 1920;

        public PointerView(Context context, float density) : base(context)
        {
            _density = density <= 0 ? 2f : density;
            SetWillNotDraw(false);
            SetLayerType(LayerType.Software, null);
            SetBackgroundColor(global::Android.Graphics.Color.Transparent);
            _fill = new global::Android.Graphics.Paint(PaintFlags.AntiAlias);
            _fill.Color = global::Android.Graphics.Color.White;
            _fill.SetStyle(global::Android.Graphics.Paint.Style.Fill);
            _stroke = new global::Android.Graphics.Paint(PaintFlags.AntiAlias);
            _stroke.Color = global::Android.Graphics.Color.Black;
            _stroke.StrokeWidth = 0.55f;
            _stroke.StrokeJoin = global::Android.Graphics.Paint.Join.Round;
            _stroke.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
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

        public void SetPoint(float nx, float ny, int screenW, int screenH)
        {
            _nx = nx;
            _ny = ny;
            _screenW = Math.Max(screenW, 1);
            _screenH = Math.Max(screenH, 1);
        }

        protected override void OnDraw(Canvas canvas)
        {
            var w = Width > 1 ? Width : _screenW;
            var h = Height > 1 ? Height : _screenH;
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
