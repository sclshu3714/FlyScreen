#if ANDROID
using Android.AccessibilityServices;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Accessibility;
using FlyScreen.Services.Programs;
using Path = Android.Graphics.Path;
using ResOrientation = Android.Content.Res.Orientation;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>无障碍：供用户程序读界面 / 点击 / 滑动（与键鼠注入共用同一服务）。</summary>
public sealed partial class FlyScreenInputAccessibilityService
{
    public string? TryGetForegroundPackage()
    {
        try
        {
            var root = RootInActiveWindow;
            if (root == null) return null;
            try
            {
                return root.PackageName?.ToString();
            }
            finally
            {
                root.Recycle();
            }
        }
        catch
        {
            return null;
        }
    }

    public (int W, int H) GetScreenPixels()
    {
        RefreshScreenSize();
        return (_screenW, _screenH);
    }

    /// <summary>当前显示旋转：0 / 90 / 180 / 270（度）。失败为 0。</summary>
    public int GetDisplayRotationDegrees()
    {
        try
        {
            _wm ??= GetSystemService(WindowService) as IWindowManager;
            global::Android.Views.Display? display = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                display = Display;
            display ??= _wm?.DefaultDisplay;
            if (display == null) return 0;
            var rot = display.Rotation;
            return rot switch
            {
                SurfaceOrientation.Rotation0 => 0,
                SurfaceOrientation.Rotation90 => 90,
                SurfaceOrientation.Rotation180 => 180,
                SurfaceOrientation.Rotation270 => 270,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 系统 UI 朝向是否横屏。优先 <see cref="Android.Content.Res.Orientation"/>，
    /// 避免 GetRealSize 滞后时把竖屏误判成横屏（从而误用横屏 OCR 区）。
    /// </summary>
    public bool? GetUiIsLandscape()
    {
        try
        {
            var orient = Resources?.Configuration?.Orientation;
            if (orient == ResOrientation.Landscape) return true;
            if (orient == ResOrientation.Portrait) return false;
        }
        catch { /* ignore */ }

        RefreshScreenSize();
        if (_screenW > 0 && _screenH > 0)
            return ProgramRegionKeys.IsLandscape(_screenW, _screenH);
        return null;
    }

    public bool ContainsVisibleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!FindFirstNodeByText(text, out var node) || node == null)
            return false;
        try { return true; }
        finally
        {
            try { node.Recycle(); } catch { /* ignore */ }
        }
    }

    public List<string> CollectVisibleTexts(int max)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var limit = Math.Clamp(max, 1, 200);
        try
        {
            try
            {
                var windows = Windows;
                if (windows != null)
                {
                    foreach (var window in windows)
                    {
                        var root = window?.Root;
                        if (root == null) continue;
                        try { WalkTexts(root, list, seen, limit); }
                        finally
                        {
                            try { root.Recycle(); } catch { /* ignore */ }
                        }
                        if (list.Count >= limit) return list;
                    }
                }
            }
            catch { /* ignore */ }

            if (list.Count == 0)
            {
                var root = RootInActiveWindow;
                if (root == null) return list;
                try { WalkTexts(root, list, seen, limit); }
                finally
                {
                    try { root.Recycle(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
        return list;
    }

    public bool TryTapText(string text)
    {
        if (!FindFirstNodeByText(text, out var node) || node == null)
            return false;
        try
        {
            if (TryGetBounds(node, out var r) && r.Width() > 0 && r.Height() > 0)
            {
                var cx = r.CenterX();
                var cy = r.CenterY();
                return RunOnUi(() =>
                {
                    DispatchTap(cx, cy);
                    return true;
                });
            }

            // 无 bounds 时尝试无障碍 Click
            return RunOnUi(() => node.PerformAction(global::Android.Views.Accessibility.Action.Click));
        }
        finally
        {
            try { node.Recycle(); } catch { /* ignore */ }
        }
    }

    public bool TryTapNormalized(double nx, double ny)
    {
        RefreshScreenSize();
        var x = (float)(Math.Clamp(nx, 0, 1) * Math.Max(_screenW - 1, 1));
        var y = (float)(Math.Clamp(ny, 0, 1) * Math.Max(_screenH - 1, 1));
        return RunOnUi(() =>
        {
            DispatchTap(x, y);
            return true;
        });
    }

    public bool TrySwipeNormalized(double x1, double y1, double x2, double y2, int durationMs)
    {
        RefreshScreenSize();
        var a = (float)(Math.Clamp(x1, 0, 1) * Math.Max(_screenW - 1, 1));
        var b = (float)(Math.Clamp(y1, 0, 1) * Math.Max(_screenH - 1, 1));
        var c = (float)(Math.Clamp(x2, 0, 1) * Math.Max(_screenW - 1, 1));
        var d = (float)(Math.Clamp(y2, 0, 1) * Math.Max(_screenH - 1, 1));
        var ms = Math.Clamp(durationMs, 50, 3000);

        // 主线程不能 Wait 回调（会死锁），只能 fire-and-forget
        if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper)
        {
            DispatchSwipe(a, b, c, d, ms);
            return true;
        }

        var done = new ManualResetEventSlim(false);
        // 回调可能在 binder 线程；用数组盒避免可见性问题
        var box = new[] { false };
        _ui.Post(() =>
        {
            try
            {
                var path = new Path();
                path.MoveTo(a, b);
                path.LineTo(c, d);
                var stroke = new GestureDescription.StrokeDescription(path, 0, ms);
                var builder = new GestureDescription.Builder();
                builder.AddStroke(stroke);
                var gesture = builder.Build();
                if (gesture == null)
                {
                    done.Set();
                    return;
                }

                var accepted = DispatchGesture(
                    gesture,
                    new SwipeGestureCallback(ok =>
                    {
                        box[0] = ok;
                        done.Set();
                    }),
                    null);
                if (!accepted)
                    done.Set();
            }
            catch
            {
                box[0] = false;
                done.Set();
            }
        });

        if (!done.Wait(ms + 2500))
            return false;
        return box[0];
    }

    public bool TryGlobalBack()
        => RunOnUi(() => PerformGlobalAction(GlobalAction.Back));

    public bool TryGlobalHome()
        => RunOnUi(() => PerformGlobalAction(GlobalAction.Home));

    public bool TryGlobalRecents()
        => RunOnUi(() => PerformGlobalAction(GlobalAction.Recents));

    public bool TryGlobalNotifications()
        => RunOnUi(() => PerformGlobalAction(GlobalAction.Notifications));

    public bool TryGlobalQuickSettings()
    {
        try
        {
            return RunOnUi(() => PerformGlobalAction(GlobalAction.QuickSettings));
        }
        catch
        {
            return false;
        }
    }

    public bool TryGlobalLockScreen()
    {
        try
        {
            // Android 9+：LockScreen = 8
            return RunOnUi(() => PerformGlobalAction((GlobalAction)8));
        }
        catch
        {
            return false;
        }
    }

    public bool TryGlobalTakeScreenshot()
    {
        try
        {
            // Android 9+：TakeScreenshot = 9
            return RunOnUi(() => PerformGlobalAction((GlobalAction)9));
        }
        catch
        {
            return false;
        }
    }

    public bool ContainsViewId(string viewId)
    {
        if (string.IsNullOrWhiteSpace(viewId)) return false;
        if (!FindFirstNodeByViewId(viewId, out var node) || node == null)
            return false;
        try { return true; }
        finally
        {
            try { node.Recycle(); } catch { /* ignore */ }
        }
    }

    public bool ContainsDesc(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        if (!FindFirstNodeByDesc(desc, out var node) || node == null)
            return false;
        try { return true; }
        finally
        {
            try { node.Recycle(); } catch { /* ignore */ }
        }
    }

    public int[]? FindTextBounds(string text)
    {
        if (!FindFirstNodeByText(text, out var node) || node == null)
            return null;
        try
        {
            if (!TryGetBounds(node, out var r) || r.Width() <= 0 || r.Height() <= 0)
                return null;
            return [r.Left, r.Top, r.Right, r.Bottom];
        }
        finally
        {
            try { node.Recycle(); } catch { /* ignore */ }
        }
    }

    public bool TryLongPressText(string text, int durationMs)
    {
        if (!FindFirstNodeByText(text, out var node) || node == null)
            return false;
        try
        {
            if (TryGetBounds(node, out var r) && r.Width() > 0 && r.Height() > 0)
                return TryLongPressPx(r.CenterX(), r.CenterY(), durationMs);
            return RunOnUi(() => node.PerformAction(global::Android.Views.Accessibility.Action.LongClick));
        }
        finally
        {
            try { node.Recycle(); } catch { /* ignore */ }
        }
    }

    public bool TryTapViewId(string viewId)
    {
        if (!FindFirstNodeByViewId(viewId, out var node) || node == null)
            return false;
        try { return TapNodeCenter(node); }
        finally
        {
            try { node.Recycle(); } catch { /* ignore */ }
        }
    }

    public bool TryTapDesc(string desc)
    {
        if (!FindFirstNodeByDesc(desc, out var node) || node == null)
            return false;
        try { return TapNodeCenter(node); }
        finally
        {
            try { node.Recycle(); } catch { /* ignore */ }
        }
    }

    public bool TryTapPx(int x, int y)
    {
        RefreshScreenSize();
        var px = Math.Clamp(x, 0, Math.Max(_screenW - 1, 0));
        var py = Math.Clamp(y, 0, Math.Max(_screenH - 1, 0));
        return RunOnUi(() =>
        {
            DispatchTap(px, py);
            return true;
        });
    }

    public bool TryDoubleTapNormalized(double nx, double ny)
    {
        RefreshScreenSize();
        var x = (float)(Math.Clamp(nx, 0, 1) * Math.Max(_screenW - 1, 1));
        var y = (float)(Math.Clamp(ny, 0, 1) * Math.Max(_screenH - 1, 1));
        return RunOnUi(() =>
        {
            DispatchTap(x, y);
            Thread.Sleep(80);
            DispatchTap(x, y);
            return true;
        });
    }

    public bool TryLongPressNormalized(double nx, double ny, int durationMs)
    {
        RefreshScreenSize();
        var x = (int)(Math.Clamp(nx, 0, 1) * Math.Max(_screenW - 1, 1));
        var y = (int)(Math.Clamp(ny, 0, 1) * Math.Max(_screenH - 1, 1));
        return TryLongPressPx(x, y, durationMs);
    }

    public bool TryLongPressPx(int x, int y, int durationMs)
    {
        var ms = Math.Clamp(durationMs <= 0 ? 700 : durationMs, 200, 5000);
        return RunOnUi(() =>
        {
            var path = new Path();
            path.MoveTo(x, y);
            var stroke = new GestureDescription.StrokeDescription(path, 0, ms);
            var builder = new GestureDescription.Builder();
            builder.AddStroke(stroke);
            var gesture = builder.Build();
            if (gesture == null) return false;
            return DispatchGesture(gesture, null, null);
        });
    }

    public bool TryScrollForward()
    {
        if (TryPerformScroll(global::Android.Views.Accessibility.Action.ScrollForward))
            return true;
        // 兜底：上滑
        return TrySwipeNormalized(0.5, 0.72, 0.5, 0.28, 320);
    }

    public bool TryScrollBackward()
    {
        if (TryPerformScroll(global::Android.Views.Accessibility.Action.ScrollBackward))
            return true;
        return TrySwipeNormalized(0.5, 0.28, 0.5, 0.72, 320);
    }

    public bool WaitForText(string text, int timeoutMs, int pollMs)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var timeout = Math.Clamp(timeoutMs, 100, 120_000);
        var poll = Math.Clamp(pollMs, 50, 5000);
        var end = System.Environment.TickCount64 + timeout;
        while (System.Environment.TickCount64 < end)
        {
            if (ContainsVisibleText(text))
                return true;
            Thread.Sleep(poll);
        }
        return ContainsVisibleText(text);
    }

    public bool WaitTextGone(string text, int timeoutMs, int pollMs)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var timeout = Math.Clamp(timeoutMs, 100, 120_000);
        var poll = Math.Clamp(pollMs, 50, 5000);
        var end = System.Environment.TickCount64 + timeout;
        while (System.Environment.TickCount64 < end)
        {
            if (!ContainsVisibleText(text))
                return true;
            Thread.Sleep(poll);
        }
        return !ContainsVisibleText(text);
    }

    public bool TryInputText(string text)
    {
        text ??= "";
        return RunOnUi(() =>
        {
            var node = FindEditable();
            if (node == null) return false;
            try
            {
                node.PerformAction(global::Android.Views.Accessibility.Action.Focus);
                return SetText(node, text);
            }
            finally
            {
                try { node.Recycle(); } catch { /* ignore */ }
            }
        });
    }

    public bool TryClearInput()
        => TryInputText("");

    public bool TryEnter()
    {
        return RunOnUi(() =>
        {
            var node = FindEditable();
            if (node == null) return false;
            try
            {
                // ACTION_IME_ENTER
                if (node.PerformAction((global::Android.Views.Accessibility.Action)0x00200000))
                    return true;
                return node.PerformAction(global::Android.Views.Accessibility.Action.Click);
            }
            finally
            {
                try { node.Recycle(); } catch { /* ignore */ }
            }
        });
    }

    private bool TapNodeCenter(AccessibilityNodeInfo node)
    {
        if (TryGetBounds(node, out var r) && r.Width() > 0 && r.Height() > 0)
        {
            return RunOnUi(() =>
            {
                DispatchTap(r.CenterX(), r.CenterY());
                return true;
            });
        }

        return RunOnUi(() => node.PerformAction(global::Android.Views.Accessibility.Action.Click));
    }

    private bool TryPerformScroll(global::Android.Views.Accessibility.Action action)
    {
        try
        {
            var root = RootInActiveWindow;
            if (root == null) return false;
            try
            {
                var scrollable = FindScrollable(root);
                if (scrollable == null) return false;
                try
                {
                    return RunOnUi(() => scrollable.PerformAction(action));
                }
                finally
                {
                    try { scrollable.Recycle(); } catch { /* ignore */ }
                }
            }
            finally
            {
                root.Recycle();
            }
        }
        catch
        {
            return false;
        }
    }

    private static AccessibilityNodeInfo? FindScrollable(AccessibilityNodeInfo node)
    {
        try
        {
            if (node.Scrollable)
                return AccessibilityNodeInfo.Obtain(node);
            for (var i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                if (child == null) continue;
                try
                {
                    var hit = FindScrollable(child);
                    if (hit != null) return hit;
                }
                finally
                {
                    try { child.Recycle(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private bool FindFirstNodeByViewId(string viewId, out AccessibilityNodeInfo? found)
    {
        found = null;
        if (string.IsNullOrWhiteSpace(viewId)) return false;
        try
        {
            var root = RootInActiveWindow;
            if (root == null) return false;
            try
            {
                var hits = root.FindAccessibilityNodeInfosByViewId(viewId);
                if (hits == null) return false;
                foreach (var n in hits)
                {
                    if (n == null) continue;
                    if (found == null) found = n;
                    else try { n.Recycle(); } catch { /* ignore */ }
                }
                return found != null;
            }
            finally
            {
                root.Recycle();
            }
        }
        catch
        {
            found = null;
            return false;
        }
    }

    private bool FindFirstNodeByDesc(string desc, out AccessibilityNodeInfo? found)
    {
        found = null;
        if (string.IsNullOrWhiteSpace(desc)) return false;
        try
        {
            var root = RootInActiveWindow;
            if (root == null) return false;
            try
            {
                found = FindNodeByDescContains(root, desc);
                return found != null;
            }
            finally
            {
                root.Recycle();
            }
        }
        catch
        {
            found = null;
            return false;
        }
    }

    private static AccessibilityNodeInfo? FindNodeByDescContains(AccessibilityNodeInfo node, string desc)
    {
        try
        {
            var d = node.ContentDescription?.ToString();
            if (!string.IsNullOrEmpty(d) && d.Contains(desc, StringComparison.Ordinal))
                return AccessibilityNodeInfo.Obtain(node);

            for (var i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                if (child == null) continue;
                try
                {
                    var hit = FindNodeByDescContains(child, desc);
                    if (hit != null) return hit;
                }
                finally
                {
                    try { child.Recycle(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private bool FindFirstNodeByText(string text, out AccessibilityNodeInfo? found)
    {
        found = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            // 遍历全部交互窗口（浮层/广告条常不在 ActiveWindow）
            try
            {
                var windows = Windows;
                if (windows != null)
                {
                    foreach (var window in windows)
                    {
                        var root = window?.Root;
                        if (root == null) continue;
                        try
                        {
                            if (TryFindTextInRoot(root, text, out found) && found != null)
                                return true;
                        }
                        finally
                        {
                            try { root.Recycle(); } catch { /* ignore */ }
                        }
                    }
                }
            }
            catch { /* 部分系统读窗口列表会抛 */ }

            var active = RootInActiveWindow;
            if (active == null) return false;
            try
            {
                return TryFindTextInRoot(active, text, out found) && found != null;
            }
            finally
            {
                try { active.Recycle(); } catch { /* ignore */ }
            }
        }
        catch
        {
            found = null;
            return false;
        }
    }

    private static bool TryFindTextInRoot(AccessibilityNodeInfo root, string text, out AccessibilityNodeInfo? found)
    {
        found = null;
        var hits = root.FindAccessibilityNodeInfosByText(text);
        if (hits != null)
        {
            foreach (var n in hits)
            {
                if (n == null) continue;
                if (found == null)
                    found = n;
                else
                    try { n.Recycle(); } catch { /* ignore */ }
            }
            if (found != null) return true;
        }

        found = FindNodeContains(root, text);
        return found != null;
    }

    private static AccessibilityNodeInfo? FindNodeContains(AccessibilityNodeInfo node, string text)
    {
        try
        {
            var t = node.Text?.ToString();
            var d = node.ContentDescription?.ToString();
            if ((!string.IsNullOrEmpty(t) && t.Contains(text, StringComparison.Ordinal))
                || (!string.IsNullOrEmpty(d) && d.Contains(text, StringComparison.Ordinal)))
                return AccessibilityNodeInfo.Obtain(node);

            for (var i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                if (child == null) continue;
                try
                {
                    var hit = FindNodeContains(child, text);
                    if (hit != null) return hit;
                }
                finally
                {
                    try { child.Recycle(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static void WalkTexts(
        AccessibilityNodeInfo node, List<string> list, HashSet<string> seen, int max)
    {
        if (list.Count >= max) return;
        try
        {
            void Add(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                s = s.Trim();
                if (s.Length == 0 || s.Length > 120) return;
                if (seen.Add(s))
                    list.Add(s);
            }

            Add(node.Text?.ToString());
            Add(node.ContentDescription?.ToString());

            for (var i = 0; i < node.ChildCount && list.Count < max; i++)
            {
                var child = node.GetChild(i);
                if (child == null) continue;
                try { WalkTexts(child, list, seen, max); }
                finally
                {
                    try { child.Recycle(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
    }

    private bool RunOnUi(Func<bool> action)
    {
        if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper)
            return action();

        var done = new ManualResetEventSlim(false);
        var ok = false;
        _ui.Post(() =>
        {
            try { ok = action(); }
            catch { ok = false; }
            finally { done.Set(); }
        });
        return done.Wait(5000) && ok;
    }

    private static bool TryGetBounds(AccessibilityNodeInfo node, out global::Android.Graphics.Rect rect)
    {
        rect = new global::Android.Graphics.Rect();
        try
        {
            node.GetBoundsInScreen(rect);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
#endif
