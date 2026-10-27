#if ANDROID
using System.Text;
using Android.AccessibilityServices;
using Android.Graphics;
using Android.Hardware;
using Android.OS;
using Android.Util;
using FlyScreen.Services;
using Java.Util.Concurrent;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Chinese;
using GmsTask = global::Android.Gms.Tasks.Task;
using MlText = Xamarin.Google.MLKit.Vision.Text.Text;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>无障碍截屏 + ML Kit 中英 OCR（供 <c>Fly.Ocr</c>）。</summary>
public sealed partial class FlyScreenInputAccessibilityService
{
    private static readonly object OcrGate = new();
    private static ITextRecognizer? s_recognizer;
    private static long _lastOcrGeomLogMs;
    private static long _lastShotFailLogMs;

    // 短时复用截屏：脚本连调多次 Ocr 时避免触发 TakeScreenshot 间隔限制（否则横屏恒空）
    private readonly object _shotCacheGate = new();
    private Bitmap? _shotCache;
    private long _shotCacheAtMs;
    private const int ShotCacheTtlMs = 900;

    /// <summary>
    /// 截取屏幕矩形并 OCR；失败 / 无文字 / API&lt;30 返回 <c>""</c>。
    /// 坐标为屏幕像素（与 Fly.ScreenWidth / ScreenHeight 同源），对角顺序可颠倒。
    /// 若截图像素尺寸与当前旋转不一致，会先旋转/缩放再裁切，避免横屏 OCR 恒为空。
    /// </summary>
    public string TryOcrRegion(int left, int top, int right, int bottom)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.R)
            return "";

        var l = Math.Min(left, right);
        var t = Math.Min(top, bottom);
        var r = Math.Max(left, right);
        var b = Math.Max(top, bottom);
        if (r <= l || b <= t)
            return "";

        Bitmap? full = null;
        Bitmap? aligned = null;
        Bitmap? crop = null;
        Bitmap? scaled = null;
        var ownFull = false;
        try
        {
            full = AcquireScreenshot(TimeSpan.FromSeconds(4), out ownFull);
            if (full == null || full.Width <= 0 || full.Height <= 0)
            {
                LogOcrGeomOnce($"OCR 截屏为空 screen={_screenW}x{_screenH} rect=({l},{t})-({r},{b})（可能安全层/限流）");
                return "";
            }

            RefreshScreenSize();
            aligned = AlignScreenshotToScreen(full, _screenW, _screenH, out var geomNote);
            var src = aligned ?? full;

            var swappedBuffer = aligned == null
                && Near(full.Width, _screenH) && Near(full.Height, _screenW)
                && (_screenW > _screenH) != (full.Width > full.Height);

            bool mapped;
            int cl, ct, cr, cb;
            if (swappedBuffer)
            {
                // 旋转失败时：先试 CCW 角点映射，识别为空再试 CW（部分机型缓冲方向相反）
                mapped = MapSwappedScreenRectToBitmap(l, t, r, b, _screenW, _screenH, full.Width, full.Height,
                    out cl, out ct, out cr, out cb, clockwise: false);
                if (mapped && string.IsNullOrEmpty(geomNote))
                    geomNote = "corner-map-ccw";
            }
            else
            {
                mapped = MapScreenRectToBitmap(l, t, r, b, _screenW, _screenH, src.Width, src.Height,
                    out cl, out ct, out cr, out cb);
            }

            if (!mapped)
            {
                LogOcrGeomOnce($"OCR 裁切无效 screen={_screenW}x{_screenH} shot={full.Width}x{full.Height} aligned={src.Width}x{src.Height} rect=({l},{t})-({r},{b}) {geomNote}");
                return "";
            }

            if (!string.IsNullOrEmpty(geomNote))
                LogOcrGeomOnce($"OCR 几何适配 screen={_screenW}x{_screenH} shot={full.Width}x{full.Height} → {src.Width}x{src.Height} {geomNote}");

            crop = Bitmap.CreateBitmap(src, cl, ct, cr - cl, cb - ct);
            if (crop == null)
                return "";

            // 小字号顶栏胶囊：放大 2 倍再识别
            var recognizeBmp = crop;
            if (crop.Width < 720 || crop.Height < 160)
            {
                scaled = UpscaleBitmap(crop, 2f);
                if (scaled != null)
                    recognizeBmp = scaled;
            }

            var text = RecognizeText(recognizeBmp) ?? "";
            if (string.IsNullOrWhiteSpace(text) && swappedBuffer)
            {
                try { scaled?.Recycle(); } catch { /* ignore */ }
                scaled = null;
                try { crop.Recycle(); } catch { /* ignore */ }
                crop = null;
                if (MapSwappedScreenRectToBitmap(l, t, r, b, _screenW, _screenH, full.Width, full.Height,
                        out cl, out ct, out cr, out cb, clockwise: true)
                    && cr > cl && cb > ct)
                {
                    LogOcrGeomOnce($"OCR 空结果，改用 CW 角点映射 screen={_screenW}x{_screenH} shot={full.Width}x{full.Height}");
                    crop = Bitmap.CreateBitmap(full, cl, ct, cr - cl, cb - ct);
                    if (crop != null)
                    {
                        recognizeBmp = crop;
                        if (crop.Width < 720 || crop.Height < 160)
                        {
                            scaled = UpscaleBitmap(crop, 2f);
                            if (scaled != null)
                                recognizeBmp = scaled;
                        }
                        text = RecognizeText(recognizeBmp) ?? "";
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(text))
                LogOcrGeomOnce($"OCR 无文字 screen={_screenW}x{_screenH} shot={full.Width}x{full.Height} crop={cl},{ct}-{cr},{cb} {geomNote}");

            return text;
        }
        catch (Exception ex)
        {
            Log.Warn("FlyOcr", "OcrRegion failed: " + ex.Message);
            return "";
        }
        finally
        {
            if (scaled != null && !ReferenceEquals(scaled, crop))
            {
                try { scaled.Recycle(); } catch { /* ignore */ }
            }
            if (crop != null && !ReferenceEquals(crop, aligned) && !ReferenceEquals(crop, full))
            {
                try { crop.Recycle(); } catch { /* ignore */ }
            }
            if (aligned != null && !ReferenceEquals(aligned, full))
            {
                try { aligned.Recycle(); } catch { /* ignore */ }
            }
            if (ownFull && full != null)
            {
                try { full.Recycle(); } catch { /* ignore */ }
            }
        }
    }

    private Bitmap? AcquireScreenshot(TimeSpan timeout, out bool callerOwns)
    {
        callerOwns = false;
        lock (_shotCacheGate)
        {
            var now = System.Environment.TickCount64;
            if (_shotCache != null && !_shotCache.IsRecycled
                && now - _shotCacheAtMs <= ShotCacheTtlMs
                && _shotCache.Width > 0 && _shotCache.Height > 0)
            {
                return _shotCache;
            }

            try { _shotCache?.Recycle(); } catch { /* ignore */ }
            _shotCache = null;
        }

        var shot = CaptureScreenBitmap(timeout);
        if (shot == null)
            return null;

        lock (_shotCacheGate)
        {
            try { _shotCache?.Recycle(); } catch { /* ignore */ }
            _shotCache = shot;
            _shotCacheAtMs = System.Environment.TickCount64;
            callerOwns = false; // 由缓存持有，OCR 结束不回收
            return _shotCache;
        }
    }

    private static Bitmap? UpscaleBitmap(Bitmap src, float scale)
    {
        try
        {
            var w = Math.Max(1, (int)(src.Width * scale));
            var h = Math.Max(1, (int)(src.Height * scale));
            return Bitmap.CreateScaledBitmap(src, w, h, true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把截图对齐到当前屏幕宽高方向：尺寸一致则原样；宽高对调则旋转 90°；否则按比例缩放。
    /// </summary>
    private static Bitmap? AlignScreenshotToScreen(Bitmap shot, int screenW, int screenH, out string note)
    {
        note = "";
        if (screenW <= 0 || screenH <= 0)
            return null;

        var bw = shot.Width;
        var bh = shot.Height;
        if (Near(bw, screenW) && Near(bh, screenH))
            return null; // 已对齐，调用方用原图

        // 宽高对调：常见于横屏时截图仍按自然竖屏缓冲
        if (Near(bw, screenH) && Near(bh, screenW))
        {
            // 先试逆时针 90°（竖→横常见映射），尺寸对不上再试顺时针
            var ccw = RotateBitmap90(shot, clockwise: false);
            if (ccw != null && Near(ccw.Width, screenW) && Near(ccw.Height, screenH))
            {
                note = "rotate=-90";
                return ccw;
            }
            try { ccw?.Recycle(); } catch { /* ignore */ }

            var cw = RotateBitmap90(shot, clockwise: true);
            if (cw != null && Near(cw.Width, screenW) && Near(cw.Height, screenH))
            {
                note = "rotate=+90";
                return cw;
            }
            try { cw?.Recycle(); } catch { /* ignore */ }
            note = "swap-unresolved";
            return null;
        }

        // 同比缩放（含小幅分辨率差异）
        if (bw > 0 && bh > 0)
        {
            var sx = screenW / (double)bw;
            var sy = screenH / (double)bh;
            if (Math.Abs(sx - sy) < 0.08)
            {
                note = $"scale≈{sx:0.###}";
                // 不预先缩放整图，交给 MapScreenRectToBitmap 按比例映射即可
                return null;
            }
        }

        note = "size-mismatch";
        return null;
    }

    private static bool MapScreenRectToBitmap(
        int l, int t, int r, int b,
        int screenW, int screenH,
        int bmpW, int bmpH,
        out int cl, out int ct, out int cr, out int cb)
    {
        cl = ct = cr = cb = 0;
        if (screenW <= 0 || screenH <= 0 || bmpW <= 0 || bmpH <= 0)
            return false;

        double sx = bmpW / (double)screenW;
        double sy = bmpH / (double)screenH;
        cl = (int)Math.Floor(l * sx);
        ct = (int)Math.Floor(t * sy);
        cr = (int)Math.Ceiling(r * sx);
        cb = (int)Math.Ceiling(b * sy);

        cl = Math.Clamp(cl, 0, bmpW);
        ct = Math.Clamp(ct, 0, bmpH);
        cr = Math.Clamp(cr, 0, bmpW);
        cb = Math.Clamp(cb, 0, bmpH);
        return cr > cl && cb > ct;
    }

    /// <summary>
    /// 屏幕横屏、截图竖屏（宽高对调）时映射四角后取包围盒。
    /// CCW：屏幕 (x,y) → 图 (y, screenW-1-x)；CW：屏幕 (x,y) → 图 (screenH-1-y, x)。
    /// </summary>
    private static bool MapSwappedScreenRectToBitmap(
        int l, int t, int r, int b,
        int screenW, int screenH,
        int bmpW, int bmpH,
        out int cl, out int ct, out int cr, out int cb,
        bool clockwise = false)
    {
        cl = ct = cr = cb = 0;
        if (screenW <= 0 || screenH <= 0 || bmpW <= 0 || bmpH <= 0)
            return false;

        void Map(int x, int y, out int bx, out int by)
        {
            int nx, ny;
            if (clockwise)
            {
                nx = screenH - 1 - y;
                ny = x;
            }
            else
            {
                nx = y;
                ny = screenW - 1 - x;
            }
            bx = (int)Math.Round(nx * (bmpW / (double)screenH));
            by = (int)Math.Round(ny * (bmpH / (double)screenW));
        }

        Map(l, t, out var x0, out var y0);
        Map(r, t, out var x1, out var y1);
        Map(l, b, out var x2, out var y2);
        Map(r, b, out var x3, out var y3);
        cl = Math.Clamp(Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)), 0, bmpW);
        ct = Math.Clamp(Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)), 0, bmpH);
        cr = Math.Clamp(Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)), 0, bmpW);
        cb = Math.Clamp(Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)), 0, bmpH);
        return cr > cl && cb > ct;
    }

    private static Bitmap? RotateBitmap90(Bitmap src, bool clockwise)
    {
        try
        {
            var m = new Matrix();
            m.PostRotate(clockwise ? 90f : -90f);
            return Bitmap.CreateBitmap(src, 0, 0, src.Width, src.Height, m, true);
        }
        catch (Exception ex)
        {
            Log.Warn("FlyOcr", "RotateBitmap90: " + ex.Message);
            return null;
        }
    }

    private static bool Near(int a, int b, int tol = 8)
        => Math.Abs(a - b) <= tol;

    private static void LogOcrGeomOnce(string msg)
    {
        var now = System.Environment.TickCount64;
        if (now - _lastOcrGeomLogMs < 3000)
            return;
        _lastOcrGeomLogMs = now;
        Log.Warn("FlyOcr", msg);
        try { AppLog.Warn(msg); } catch { /* ignore */ }
    }

    private Bitmap? CaptureScreenBitmap(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IExecutorService? executor = null;
        try
        {
            executor = Executors.NewSingleThreadExecutor();
            var displayId = Display?.DisplayId ?? 0;
            TakeScreenshot(displayId, executor!, new ShotCallback(tcs));
            if (!tcs.Task.Wait(timeout))
                return null;
            return tcs.Task.Result;
        }
        catch (Exception ex)
        {
            Log.Warn("FlyOcr", "TakeScreenshot failed: " + ex.Message);
            return null;
        }
        finally
        {
            try { executor?.Shutdown(); } catch { /* ignore */ }
        }
    }

    private static string? RecognizeText(Bitmap bitmap)
    {
        InputImage? image = null;
        try
        {
            var recognizer = GetRecognizer();
            image = InputImage.FromBitmap(bitmap, 0);
            var text = AwaitGmsResult(recognizer.Process(image), TimeSpan.FromSeconds(12));
            if (text == null)
                return null;
            var raw = text.GetText();
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            return NormalizeOcrText(raw);
        }
        catch (Exception ex)
        {
            Log.Warn("FlyOcr", "ML Kit failed: " + ex.Message);
            return null;
        }
        finally
        {
            try { image?.Dispose(); } catch { /* ignore */ }
        }
    }

    private static ITextRecognizer GetRecognizer()
    {
        lock (OcrGate)
        {
            if (s_recognizer != null)
                return s_recognizer;
            var options = new ChineseTextRecognizerOptions.Builder().Build();
            s_recognizer = TextRecognition.GetClient(options);
            return s_recognizer;
        }
    }

    private static MlText? AwaitGmsResult(GmsTask gms, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<MlText?>(TaskCreationOptions.RunContinuationsAsynchronously);
        gms.AddOnSuccessListener(new SuccessListener(o => tcs.TrySetResult(o as MlText)));
        gms.AddOnFailureListener(new FailureListener(_ => tcs.TrySetResult(null)));
        return tcs.Task.Wait(timeout) ? tcs.Task.Result : null;
    }

    private static string NormalizeOcrText(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        using var reader = new StringReader(raw);
        string? line;
        var first = true;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!first)
                sb.Append('\n');
            sb.Append(trimmed);
            first = false;
        }
        return sb.ToString();
    }

    private sealed class ShotCallback : Java.Lang.Object, ITakeScreenshotCallback
    {
        private readonly TaskCompletionSource<Bitmap?> _tcs;

        public ShotCallback(TaskCompletionSource<Bitmap?> tcs) => _tcs = tcs;

        public void OnSuccess(AccessibilityService.ScreenshotResult? screenshot)
        {
            if (screenshot == null)
            {
                _tcs.TrySetResult(null);
                return;
            }

            HardwareBuffer? buffer = null;
            try
            {
                buffer = screenshot.HardwareBuffer;
                if (buffer == null)
                {
                    _tcs.TrySetResult(null);
                    return;
                }

                using var hwBmp = Bitmap.WrapHardwareBuffer(buffer, screenshot.ColorSpace);
                var soft = hwBmp?.Copy(Bitmap.Config.Argb8888!, false);
                _tcs.TrySetResult(soft);
            }
            catch
            {
                _tcs.TrySetResult(null);
            }
            finally
            {
                try { buffer?.Close(); } catch { /* ignore */ }
            }
        }

        public void OnFailure(int errorCode)
        {
            var now = System.Environment.TickCount64;
            if (now - _lastShotFailLogMs > 2000)
            {
                _lastShotFailLogMs = now;
                // 3=间隔过短 6=安全窗口(FLAG_SECURE) 等
                var hint = errorCode switch
                {
                    3 => "间隔过短",
                    6 => "安全窗口(无法截屏)",
                    _ => "见系统码"
                };
                Log.Warn("FlyOcr", $"TakeScreenshot OnFailure code={errorCode} ({hint})");
                try { AppLog.Warn($"OCR截屏失败 code={errorCode} ({hint})"); } catch { /* ignore */ }
            }
            _tcs.TrySetResult(null);
        }
    }

    private sealed class SuccessListener : Java.Lang.Object, global::Android.Gms.Tasks.IOnSuccessListener
    {
        private readonly Action<Java.Lang.Object?> _onSuccess;
        public SuccessListener(Action<Java.Lang.Object?> onSuccess) => _onSuccess = onSuccess;
        public void OnSuccess(Java.Lang.Object? result) => _onSuccess(result);
    }

    private sealed class FailureListener : Java.Lang.Object, global::Android.Gms.Tasks.IOnFailureListener
    {
        private readonly Action<Java.Lang.Exception> _onFailure;
        public FailureListener(Action<Java.Lang.Exception> onFailure) => _onFailure = onFailure;
        public void OnFailure(Java.Lang.Exception e) => _onFailure(e);
    }
}
#endif
