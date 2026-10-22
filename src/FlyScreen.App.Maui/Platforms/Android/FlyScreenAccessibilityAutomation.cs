#if ANDROID
using Android.Graphics;
using Android.OS;
using Android.Views.Accessibility;
using Path = Android.Graphics.Path;

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
        try
        {
            var root = RootInActiveWindow;
            if (root == null) return list;
            try
            {
                WalkTexts(root, list, seen, Math.Clamp(max, 1, 200));
            }
            finally
            {
                root.Recycle();
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
        return RunOnUi(() =>
        {
            DispatchSwipe(a, b, c, d, ms);
            return true;
        });
    }

    public bool TryGlobalBack()
        => RunOnUi(() => PerformGlobalAction(global::Android.AccessibilityServices.GlobalAction.Back));

    public bool TryGlobalHome()
        => RunOnUi(() => PerformGlobalAction(global::Android.AccessibilityServices.GlobalAction.Home));

    private bool FindFirstNodeByText(string text, out AccessibilityNodeInfo? found)
    {
        found = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var root = RootInActiveWindow;
            if (root == null) return false;
            try
            {
                // 先精确 API
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

                // 再子串遍历（部分机型 FindByText 不准）
                found = FindNodeContains(root, text);
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
