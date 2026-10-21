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
    private bool _sessionActive;
    /// <summary>false 时只保留手势注入坐标，不画无障碍指针（改由系统悬浮窗画，后台也能看见）。</summary>
    private bool _drawCursor = true;
    private bool _reportedAddFailure;
    private bool _shift;
    private bool _ctrl;
    private bool _caps;
    private int _screenW = 1080;
    private int _screenH = 1920;

    // 左键按住拖拽：GestureDescription willContinue + continueStroke
    private GestureDescription.StrokeDescription? _dragStroke;
    private bool _fingerDown;
    private bool _gestureBusy;
    private bool _pendingUp;
    private float _gx;
    private float _gy;
    private float _pendingX;
    private float _pendingY;
    private bool _hasPendingMove;

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
            _sessionActive = true;
            _cursorWanted = _drawCursor;
        }
        ScheduleCursor();
    }

    public void EndSession()
    {
        lock (_cursorGate)
        {
            _sessionActive = false;
            _cursorWanted = false;
        }
        ScheduleCursor();
        _ui.Post(() =>
        {
            CancelDrag();
            ReleaseRemoteSession();
        });
    }

    /// <summary>
    /// 有「显示在其他应用上层」时关掉无障碍箭头，改用系统悬浮窗，避免切到其他应用后看不见指针。
    /// </summary>
    public void SetDrawCursor(bool enabled)
    {
        lock (_cursorGate)
        {
            _drawCursor = enabled;
            _cursorWanted = _sessionActive && _drawCursor;
        }
        ScheduleCursor();
    }

    public void MoveAbsolute(double nx, double ny)
    {
        lock (_cursorGate)
        {
            _nx = (float)Math.Clamp(nx, 0, 1);
            _ny = (float)Math.Clamp(ny, 0, 1);
            _cursorWanted = _sessionActive && _drawCursor;
        }
        if (_drawCursor)
            ScheduleCursor();
        if (_fingerDown)
            _ui.Post(QueueDragMove);
    }

    public void MoveRelative(int dx, int dy)
    {
        lock (_cursorGate)
        {
            // 与悬浮窗一致：按 1920×1080 参考桌面换算，避免电脑像素在手机上乱跳
            _nx = Math.Clamp(_nx + dx / 1920f, 0f, 1f);
            _ny = Math.Clamp(_ny + dy / 1080f, 0f, 1f);
            _cursorWanted = _sessionActive && _drawCursor;
        }
        if (_drawCursor)
            ScheduleCursor();
        if (_fingerDown)
            _ui.Post(QueueDragMove);
    }

    public void Click(int button, bool down)
    {
        // 右键：长按
        if (button == 1)
        {
            if (!down) return;
            var (x, y) = ToPixels();
            DispatchLongPress(x, y);
            _ui.PostDelayed(RememberExternalTarget, 320);
            return;
        }

        // 左键：按下开始拖拽手势，抬起结束（支持按住拖动）
        if (button != 0) return;
        if (down)
            _ui.Post(BeginDrag);
        else
            _ui.Post(EndDrag);
    }

    public void Wheel(int delta, bool horizontal)
    {
        if (_fingerDown) return; // 拖拽中不打滚轮手势，避免打断
        var (x, y) = ToPixels();
        // 桌面一格约 ±120；原先 /3 只有约 40px，文本几乎不动
        var notches = Math.Max(1f, Math.Abs(delta) / 120f);
        var dist = Math.Clamp(notches * 280f, 160f, 900f);
        float x2 = x, y2 = y;
        if (horizontal)
            x2 = delta > 0 ? x - dist : x + dist;
        else
            y2 = delta > 0 ? y - dist : y + dist;
        DispatchSwipe(x, y, x2, y2, 70);
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
                break;
            case 0x11 or 0xA2 or 0xA3:
                _ctrl = down;
                break;
            case 0x14:
                if (down) _caps = !_caps;
                break;
            case 0x12 or 0xA4 or 0xA5:
                return;
        }

        // 程序编辑器：跨屏按键直写 Monaco，不走透明 IME 代理
        if (RemoteTextInputHub.HasSink
            && RemoteTextInputHub.TryHandleRemoteKey(vk, down, _shift, _caps, _ctrl))
            return;

        var keyCode = VkToAndroidKeyCode(vk);
        if (!down)
        {
            if (keyCode > 0)
                _ime?.Dispatch(keyCode, false, false);
            return;
        }

        if (vk is 0x10 or 0xA0 or 0xA1)
        {
            _ime?.Dispatch((int)global::Android.Views.Keycode.ShiftLeft, down, false);
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

        // 非编辑器：IME 代理（其它 App 输入框）
        RememberExternalTarget();
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
            var text = RemoteVkMap.ToChar(vk, upper);
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

        // 先钉住外部输入框，再给 IME 代理焦点，避免提交时目标节点丢失
        RememberExternalTarget();
        if (!EnsureIme()) return false;

        var before = _ime!.TextSnapshot;
        var shift = vk is >= 0x41 and <= 0x5A ? (_shift ^ _caps) : _shift;
        var dispatched = _ime.Dispatch(keyCode, true, shift);
        if (!dispatched)
            return false;

        // 已组字或代理框已出字：由 OnImeCommitted 写入目标
        if (_ime.IsComposing)
            return true;
        if (!string.Equals(_ime.TextSnapshot, before, StringComparison.Ordinal))
            return true;

        // 中文 IME 有时吞掉 KeyEvent 却不组字：放行给 ACTION_SET_TEXT，保证至少能输入
        return false;
    }

    private bool EnsureIme()
    {
        EnsureForeground();
        EnsureCursorOverlay();
        RefreshScreenSize();
        if (_wm == null) return false;
        if (!_cursorVisible && TryAddCursor())
            _cursorVisible = true;
        RememberExternalTarget();
        _ime ??= new FlyScreenImeProxy(this, OnImeCommitted, OnImeDeleted);
        return _ime.Ensure(_wm, _overlayType);
    }

    /// <summary>电脑侧组字确认的文本（不经本机软键盘）。</summary>
    public void InsertCommittedText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (RemoteTextInputHub.TryAcceptText(text))
            return;

        var node = CurrentTarget() ?? FindEditable() ?? FindEditableAtCursor();
        if (node == null) return;
        if (!ReferenceEquals(node, _imeTarget)
            && !string.Equals(node.PackageName?.ToString(), PackageName, StringComparison.Ordinal))
        {
            _imeTarget?.Dispose();
            _imeTarget = node;
        }
        if (text == "\n")
            SubmitOrNewline(node);
        else
            InsertText(node, text);
    }

    private void OnImeCommitted(string text)
    {
        if (text.Length == 0) return;
        // 编辑器已由 RemoteTextInputHub 接管；此处仅服务其它 App 输入框
        if (RemoteTextInputHub.HasSink)
            return;

        InsertCommittedText(text);
    }

    private void OnImeDeleted(int utf16Count)
    {
        if (utf16Count <= 0) return;
        if (RemoteTextInputHub.HasSink)
            return;

        var node = CurrentTarget();
        if (node == null) return;
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
        // 优先：输入焦点；其次：指针坐标下的可编辑控件（点击后焦点可能尚未就绪）
        var found = FindEditable() ?? FindEditableAtCursor();
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
                // 即使焦点被 IME 代理抢走，节点引用仍可用于 ACTION_SET_TEXT
                if (_imeTarget.Refresh()
                    && !string.Equals(_imeTarget.PackageName?.ToString(), PackageName, StringComparison.Ordinal)
                    && IsEditable(_imeTarget))
                    return _imeTarget;
            }
            catch
            {
                /* 节点已失效 */
            }
            try { _imeTarget.Dispose(); } catch { /* ignore */ }
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

    private static int VkToAndroidKeyCode(ushort vk) => RemoteVkMap.ToAndroidKeyCode(vk);

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

    /// <summary>指针坐标下的可编辑节点（点击后焦点尚未到齐时用）。</summary>
    private global::Android.Views.Accessibility.AccessibilityNodeInfo? FindEditableAtCursor()
    {
        var (px, py) = ToPixels();
        try
        {
            var windows = Windows;
            if (windows != null)
            {
                foreach (var window in windows)
                {
                    var found = TakeEditableAt(window?.Root, px, py);
                    if (found != null) return found;
                }
            }
        }
        catch
        {
            /* ignore */
        }
        return TakeEditableAt(RootInActiveWindow, px, py);
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

    private global::Android.Views.Accessibility.AccessibilityNodeInfo? TakeEditableAt(
        global::Android.Views.Accessibility.AccessibilityNodeInfo? root, float x, float y)
    {
        if (root == null) return null;
        try
        {
            var hit = FindEditableHit(root, x, y);
            if (hit != null
                && !string.Equals(hit.PackageName?.ToString(), PackageName, StringComparison.Ordinal))
            {
                if (!ReferenceEquals(hit, root))
                    root.Dispose();
                return hit;
            }
            hit?.Dispose();
        }
        catch
        {
            /* ignore */
        }
        root.Dispose();
        return null;
    }

    private static global::Android.Views.Accessibility.AccessibilityNodeInfo? FindEditableHit(
        global::Android.Views.Accessibility.AccessibilityNodeInfo node, float x, float y)
    {
        var bounds = new global::Android.Graphics.Rect();
        node.GetBoundsInScreen(bounds);
        if (!bounds.Contains((int)x, (int)y))
            return null;

        global::Android.Views.Accessibility.AccessibilityNodeInfo? best = null;
        var count = node.ChildCount;
        for (var i = 0; i < count; i++)
        {
            global::Android.Views.Accessibility.AccessibilityNodeInfo? child = null;
            try
            {
                child = node.GetChild(i);
                if (child == null) continue;
                var deeper = FindEditableHit(child, x, y);
                if (deeper != null)
                {
                    if (!ReferenceEquals(deeper, child))
                        child.Dispose();
                    best?.Dispose();
                    best = deeper;
                }
            }
            catch
            {
                try { child?.Dispose(); } catch { /* ignore */ }
            }
        }

        if (best != null)
            return best;
        if (IsEditable(node))
            return global::Android.Views.Accessibility.AccessibilityNodeInfo.Obtain(node);
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

    /// <summary>远程光标屏幕像素（供组字浮层锚定）。</summary>
    public bool TryGetCursorScreenPixels(out int x, out int y)
    {
        var (fx, fy) = ToPixels();
        x = (int)Math.Round(fx);
        y = (int)Math.Round(fy);
        return true;
    }

    /// <summary>当前可编辑焦点框大致插入点（框底边左侧），用于非 Monaco 场景。</summary>
    public bool TryGetFocusedEditableCaretScreenPixels(out int x, out int y)
    {
        x = y = 0;
        try
        {
            var node = CurrentTarget() ?? FindEditable() ?? FindEditableAtCursor();
            if (node == null) return false;
            var bounds = new global::Android.Graphics.Rect();
            node.GetBoundsInScreen(bounds);
            if (bounds.Width() <= 0 || bounds.Height() <= 0) return false;
            x = bounds.Left + Math.Min(24, bounds.Width() / 4);
            y = bounds.Bottom;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private (float x, float y) ToPixels()
    {
        // 有系统悬浮指针时，以箭头尖真实屏幕坐标为准，消除「指针在 V、点到 G」类上下偏移
        if (FlyScreenCursorOverlay.TryGetHotspotPixels(out var hx, out var hy))
            return (hx, hy);

        RefreshScreenSize();
        lock (_cursorGate)
            return (_nx * Math.Max(_screenW - 1, 1), _ny * Math.Max(_screenH - 1, 1));
    }

    private void RefreshScreenSize()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var max = _wm?.MaximumWindowMetrics?.Bounds;
                if (max != null && max.Width() > 0 && max.Height() > 0)
                {
                    _screenW = max.Width();
                    _screenH = max.Height();
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
            /* 旧接口失败时用 DisplayMetrics */
        }

        var dm = Resources?.DisplayMetrics;
        if (dm == null) return;
        _screenW = dm.WidthPixels;
        _screenH = dm.HeightPixels;
    }

    private void BeginDrag()
    {
        if (_fingerDown) return;
        var (x, y) = ToPixels();
        _gx = x;
        _gy = y;
        _pendingX = x;
        _pendingY = y;
        _hasPendingMove = false;
        _pendingUp = false;
        _fingerDown = true;

        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            DispatchTap(x, y);
            _fingerDown = false;
            _ui.PostDelayed(RememberExternalTarget, 320);
            return;
        }

        var path = new Path();
        path.MoveTo(x, y);
        try
        {
            _dragStroke = new GestureDescription.StrokeDescription(path, 0, 40, willContinue: true);
            DispatchOngoingStroke();
        }
        catch (Exception ex)
        {
            Log.Warn("FlyScreen", "begin drag: " + ex.Message);
            _dragStroke = null;
            _fingerDown = false;
            DispatchTap(x, y);
        }
    }

    private void QueueDragMove()
    {
        if (!_fingerDown || _dragStroke == null) return;
        var (x, y) = ToPixels();
        _pendingX = x;
        _pendingY = y;
        _hasPendingMove = true;
        if (!_gestureBusy)
            FlushDragQueue();
    }

    private void EndDrag()
    {
        if (!_fingerDown)
            return;
        _pendingUp = true;
        if (!_gestureBusy)
            FlushDragQueue();
    }

    private void CancelDrag()
    {
        _fingerDown = false;
        _dragStroke = null;
        _gestureBusy = false;
        _hasPendingMove = false;
        _pendingUp = false;
    }

    private void FlushDragQueue()
    {
        if (_gestureBusy || _dragStroke == null) return;

        if (_hasPendingMove)
        {
            _hasPendingMove = false;
            var x = _pendingX;
            var y = _pendingY;
            var dx = x - _gx;
            var dy = y - _gy;
            if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f)
            {
                if (_pendingUp)
                    FinishDragStroke();
                return;
            }

            var path = new Path();
            path.MoveTo(_gx, _gy);
            path.LineTo(x, y);
            var duration = (long)Math.Clamp(Math.Sqrt(dx * dx + dy * dy), 16, 80);
            try
            {
                // 移动段保持 willContinue；抬起由 FinishDragStroke 收尾
                _dragStroke = _dragStroke.ContinueStroke(path, 0, duration, willContinue: true);
                _gx = x;
                _gy = y;
                DispatchOngoingStroke();
            }
            catch (Exception ex)
            {
                Log.Warn("FlyScreen", "drag move: " + ex.Message);
                CancelDrag();
            }
            return;
        }

        if (_pendingUp)
            FinishDragStroke();
    }

    private void FinishDragStroke()
    {
        if (_dragStroke == null)
        {
            CancelDrag();
            return;
        }

        var path = new Path();
        path.MoveTo(_gx, _gy);
        try
        {
            _dragStroke = _dragStroke.ContinueStroke(path, 0, 20, willContinue: false);
            _pendingUp = false;
            _fingerDown = false;
            DispatchOngoingStroke(ending: true);
        }
        catch (Exception ex)
        {
            Log.Warn("FlyScreen", "drag end: " + ex.Message);
            CancelDrag();
            DispatchTap(_gx, _gy);
        }
    }

    private void DispatchOngoingStroke(bool ending = false)
    {
        if (_dragStroke == null) return;
        var builder = new GestureDescription.Builder();
        builder.AddStroke(_dragStroke);
        var gesture = builder.Build();
        if (gesture == null)
        {
            CancelDrag();
            return;
        }

        _gestureBusy = true;
        var ok = DispatchGesture(gesture, new DragGestureCallback(this, ending), null);
        if (!ok)
        {
            _gestureBusy = false;
            if (ending)
            {
                CancelDrag();
                _ui.PostDelayed(RememberExternalTarget, 320);
            }
            else
            {
                // 派发失败时降级为点击，避免卡死
                var (x, y) = (_gx, _gy);
                CancelDrag();
                DispatchTap(x, y);
            }
        }
    }

    private void OnDragGestureDone(bool ending, bool completed)
    {
        _gestureBusy = false;
        if (ending || !completed)
        {
            if (ending)
                _ui.PostDelayed(RememberExternalTarget, 320);
            _dragStroke = null;
            _fingerDown = false;
            _pendingUp = false;
            _hasPendingMove = false;
            return;
        }

        FlushDragQueue();
    }

    private sealed class DragGestureCallback : AccessibilityService.GestureResultCallback
    {
        private readonly FlyScreenInputAccessibilityService _owner;
        private readonly bool _ending;

        public DragGestureCallback(FlyScreenInputAccessibilityService owner, bool ending)
        {
            _owner = owner;
            _ending = ending;
        }

        public override void OnCompleted(GestureDescription? gestureDescription)
            => _owner.OnDragGestureDone(_ending, completed: true);

        public override void OnCancelled(GestureDescription? gestureDescription)
            => _owner.OnDragGestureDone(_ending, completed: false);
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
