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
using CompressFormat = Android.Graphics.Bitmap.CompressFormat;

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

    /// <summary>最近一次 TakeScreenshot 失败说明（供抽样 txt / Toast）。</summary>
    private string _lastShotFailDetail = "";
    // 防止 Java 回调被 GC 提前回收导致一直等不到 OnSuccess/OnFailure
    private ShotCallback? _pinnedShotCallback;

    /// <summary>
    /// 截取屏幕矩形并 OCR；失败 / 无文字 / API&lt;30 返回 <c>""</c>。
    /// 坐标为屏幕像素（与 Fly.ScreenWidth / ScreenHeight 同源），对角顺序可颠倒。
    /// 若截图像素尺寸与当前旋转不一致，会先旋转/缩放再裁切，避免横屏 OCR 恒为空。
    /// </summary>
    public string TryOcrRegion(int left, int top, int right, int bottom)
        => TryOcrRegionCore(left, top, right, bottom, savePngPath: null);

    /// <summary>公共目录：文件管理 → 下载 → XuanNiao → ocr_samples。</summary>
    public const string OcrSamplePublicRelativeDir = AndroidXuanniaoDownloads.MediaRelativeRoot + "/ocr_samples";

    /// <summary>
    /// 标定抽样：截取识别区 → OCR → 保存到 <c>下载/XuanNiao/ocr_samples</c>（PNG + 识别 TXT）。
    /// </summary>
    public string? TrySampleOcrRegion(int left, int top, int right, int bottom, string? fileStem, out string ocrText)
    {
        ocrText = "";
        var stem = string.IsNullOrWhiteSpace(fileStem) ? "ocr_sample" : SanitizeFileStem(fileStem);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // 文件名避免过长；明确 ocr_ 前缀，勿与占位文件混淆
        if (stem.Length > 40) stem = stem[..40];
        var pngName = $"ocr_{stem}_{stamp}.png";
        var txtName = $"ocr_{stem}_{stamp}.txt";

        var ctx = global::Android.App.Application.Context;
        var cacheDir = ctx?.CacheDir?.AbsolutePath;
        if (string.IsNullOrEmpty(cacheDir))
        {
            AppLog.Warn("抽样失败：无 CacheDir");
            return null;
        }

        var tempPng = System.IO.Path.Combine(cacheDir, pngName);
        try
        {
            if (File.Exists(tempPng))
                File.Delete(tempPng);
        }
        catch { /* ignore */ }

        _lastShotFailDetail = "";
        AppLog.Info($"抽样开始 rect=({left},{top})-({right},{bottom}) → {pngName}");
        ocrText = TryOcrRegionCore(left, top, right, bottom, tempPng);

        if (!File.Exists(tempPng) || new FileInfo(tempPng).Length <= 0)
        {
            var failHint = string.IsNullOrWhiteSpace(_lastShotFailDetail)
                ? "未能截取识别区图片（检查无障碍已开、Android 11+、目标页未禁止截屏）"
                : _lastShotFailDetail;
            var failBody = string.IsNullOrWhiteSpace(ocrText)
                ? "抽样失败：" + failHint
                : ocrText;
            try
            {
                AndroidXuanniaoDownloads.PublishText(failBody, "ocr_samples", txtName);
            }
            catch { /* ignore */ }

            AppLog.Warn("抽样失败：未生成临时 PNG — " + failHint);
            ocrText = failBody;
            return null;
        }

        var publicPng = AndroidXuanniaoDownloads.PublishFile(tempPng, "ocr_samples", pngName, "image/png");
        // PNG 失败再试 JPEG
        if (string.IsNullOrEmpty(publicPng))
        {
            var jpgName = $"ocr_{stem}_{stamp}.jpg";
            var tempJpg = System.IO.Path.Combine(cacheDir, jpgName);
            try
            {
                if (TryConvertPngFileToJpeg(tempPng, tempJpg)
                    && File.Exists(tempJpg))
                {
                    publicPng = AndroidXuanniaoDownloads.PublishFile(tempJpg, "ocr_samples", jpgName, "image/jpeg");
                    try { File.Delete(tempJpg); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("FlyOcr", "jpeg fallback: " + ex.Message);
            }
        }

        try { File.Delete(tempPng); } catch { /* ignore */ }

        var ocrBody = string.IsNullOrWhiteSpace(ocrText) ? "(无文字)" : ocrText;
        try
        {
            AndroidXuanniaoDownloads.PublishText(ocrBody, "ocr_samples", txtName);
        }
        catch (Exception ex)
        {
            Log.Warn("FlyOcr", "sample txt: " + ex.Message);
        }

        if (string.IsNullOrEmpty(publicPng))
        {
            // 图没写出去时，仍把识别文案落盘到 txt，避免只剩旧版 keep 占位被误认
            AppLog.Warn("抽样图未能写入「下载/XuanNiao/ocr_samples」（OCR 文案已尽量写入同名 txt）");
            return string.IsNullOrWhiteSpace(ocrText) ? null : $"{OcrSamplePublicRelativeDir}/{txtName}";
        }

        AppLog.Info($"抽样完成 PNG={publicPng} OCR={ocrBody}");
        return publicPng;
    }

    private static bool TryConvertPngFileToJpeg(string pngPath, string jpgPath)
    {
        Bitmap? bmp = null;
        try
        {
            bmp = BitmapFactory.DecodeFile(pngPath);
            if (bmp == null) return false;
            using var fs = new FileStream(jpgPath, FileMode.Create, FileAccess.Write);
            return bmp.Compress(CompressFormat.Jpeg!, 92, fs);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { bmp?.Recycle(); } catch { /* ignore */ }
        }
    }

    private static string SanitizeFileStem(string stem)
    {
        var sb = new StringBuilder(stem.Length);
        foreach (var ch in stem.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')
                sb.Append(ch);
            else
                sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(s) ? "ocr_sample" : s;
    }

    private static bool TrySavePng(Bitmap bmp, string path)
    {
        try
        {
            var parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            if (!bmp.Compress(CompressFormat.Png!, 100, fs))
                return false;
            fs.Flush();
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("FlyOcr", "save png: " + ex.Message);
            return false;
        }
    }

    private string TryOcrRegionCore(int left, int top, int right, int bottom, string? savePngPath)
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
            // DrawRect 是 ApplicationOverlay，会被 TakeScreenshot 一并截入；半透明红罩盖住白字会导致 ML Kit 空结果
            HideDebugRectBeforeOcr();

            full = AcquireScreenshot(TimeSpan.FromSeconds(5), out ownFull);
            if (full == null || full.Width <= 0 || full.Height <= 0)
            {
                if (string.IsNullOrWhiteSpace(_lastShotFailDetail))
                    _lastShotFailDetail = "截屏为空（可能限流/安全窗口/显示 ID 无效）";
                LogOcrGeomOnce($"OCR 截屏为空 screen={_screenW}x{_screenH} rect=({l},{t})-({r},{b}) {_lastShotFailDetail}");
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

            if (!string.IsNullOrEmpty(savePngPath))
                TrySavePng(crop, savePngPath);

            var text = RecognizeCropWithRetries(crop, ref scaled);
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
                        if (!string.IsNullOrEmpty(savePngPath))
                            TrySavePng(crop, savePngPath);
                        text = RecognizeCropWithRetries(crop, ref scaled);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                LogOcrGeomOnce(
                    $"OCR 无文字 screen={_screenW}x{_screenH} shot={full.Width}x{full.Height} crop={cl},{ct}-{cr},{cb} size={cr - cl}x{cb - ct} {geomNote}");
            }
            else
            {
                try
                {
                    Console.WriteLine(
                        $"OCR hit screen={_screenW}x{_screenH} shot={full.Width}x{full.Height} crop={cl},{ct}-{cr},{cb} len={text.Length}");
                }
                catch { /* ignore */ }
            }

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

    /// <summary>OCR 前撤掉调试红框并清截屏缓存，避免红罩进图 / 脏缓存。</summary>
    private void HideDebugRectBeforeOcr()
    {
        void Hide()
        {
            try { RemoveDebugRectView_NoPost(); } catch { /* ignore */ }
            InvalidateShotCache();
        }

        if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper)
        {
            Hide();
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try { Hide(); }
            finally { tcs.TrySetResult(true); }
        });
        tcs.Task.Wait(TimeSpan.FromMilliseconds(800));
        // 等一帧合成，避免撤窗后立刻截仍带旧层
        try { System.Threading.Thread.Sleep(50); } catch { /* ignore */ }
    }

    private void InvalidateShotCache()
    {
        lock (_shotCacheGate)
        {
            try { _shotCache?.Recycle(); } catch { /* ignore */ }
            _shotCache = null;
            _shotCacheAtMs = 0;
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

    /// <summary>顶栏小字：放大后再认；空则试 90°/270°（缓冲方向偶发反了）。</summary>
    private static string RecognizeCropWithRetries(Bitmap crop, ref Bitmap? scaled)
    {
        var recognizeBmp = crop;
        var minW = 960;
        var minH = 220;
        if (crop.Width < minW || crop.Height < minH)
        {
            var sx = Math.Max(2f, minW / (float)Math.Max(crop.Width, 1));
            var sy = Math.Max(2f, minH / (float)Math.Max(crop.Height, 1));
            var scale = Math.Min(3.5f, Math.Max(sx, sy));
            scaled = UpscaleBitmap(crop, scale);
            if (scaled != null)
                recognizeBmp = scaled;
        }

        // 非水平文字：先正交角，再轻量倾斜（±15/±30）
        foreach (var deg in new[] { 0, 90, 270, 180 })
        {
            var text = RecognizeText(recognizeBmp, rotationDegrees: deg) ?? "";
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (deg != 0)
                {
                    try { Console.WriteLine($"OCR hit rotation={deg} len={text.Length}"); } catch { /* ignore */ }
                }
                return PreferHintish(text);
            }
        }

        foreach (var tilt in new[] { 15f, -15f, 30f, -30f })
        {
            Bitmap? tilted = null;
            try
            {
                tilted = RotateBitmapDegrees(recognizeBmp, tilt);
                if (tilted == null) continue;
                var text = RecognizeText(tilted, rotationDegrees: 0) ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { Console.WriteLine($"OCR hit tilt={tilt:0} len={text.Length}"); } catch { /* ignore */ }
                    return PreferHintish(text);
                }
            }
            finally
            {
                if (tilted != null && !ReferenceEquals(tilted, recognizeBmp))
                {
                    try { tilted.Recycle(); } catch { /* ignore */ }
                }
            }
        }

        return "";
    }

    /// <summary>多段 OCR 结果时优先保留含「上滑/继续观看」的行。</summary>
    private static string PreferHintish(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length <= 1) return text;
        var hit = lines.FirstOrDefault(l =>
            l.Contains("上滑", StringComparison.Ordinal)
            || l.Contains("继续观看", StringComparison.Ordinal));
        return string.IsNullOrEmpty(hit) ? text : hit;
    }

    private static Bitmap? RotateBitmapDegrees(Bitmap src, float degrees)
    {
        try
        {
            var m = new Matrix();
            m.PostRotate(degrees);
            return Bitmap.CreateBitmap(src, 0, 0, src.Width, src.Height, m, true);
        }
        catch (Exception ex)
        {
            Log.Warn("FlyOcr", "RotateBitmapDegrees: " + ex.Message);
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
        try { Console.WriteLine(msg); } catch { /* ignore */ }
    }

    private Bitmap? CaptureScreenBitmap(TimeSpan timeout)
    {
        // 优先当前 Display，失败再试默认屏 0（部分横屏机型 DisplayId 异常）
        var ids = new List<int>();
        try
        {
            var id = Display?.DisplayId ?? 0;
            ids.Add(id);
            if (id != 0) ids.Add(0);
        }
        catch
        {
            ids.Add(0);
        }

        foreach (var displayId in ids)
        {
            // 间隔过短时最多再试 2 次
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    try { System.Threading.Thread.Sleep(350 * attempt); } catch { /* ignore */ }
                }

                var bmp = CaptureScreenBitmapOnce(displayId, timeout);
                if (bmp != null && bmp.Width > 0 && bmp.Height > 0)
                {
                    _lastShotFailDetail = "";
                    return bmp;
                }

                // 非「间隔过短」不必空转重试
                if (!_lastShotFailDetail.Contains("间隔过短", StringComparison.Ordinal))
                    break;
            }
        }

        return null;
    }

    private Bitmap? CaptureScreenBitmapOnce(int displayId, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IExecutorService? executor = null;
        ShotCallback? callback = null;
        try
        {
            executor = Executors.NewSingleThreadExecutor();
            callback = new ShotCallback(tcs, detail => _lastShotFailDetail = detail);
            _pinnedShotCallback = callback; // 钉住 JNI 回调，避免 GC

            // 部分 OEM 要求在主线程发起 TakeScreenshot
            void Start()
            {
                try
                {
                    TakeScreenshot(displayId, executor!, callback);
                }
                catch (Exception ex)
                {
                    _lastShotFailDetail = "TakeScreenshot 异常: " + ex.Message;
                    tcs.TrySetResult(null);
                }
            }

            if (Looper.MainLooper != null && Looper.MyLooper() != Looper.MainLooper)
                _ui.Post(Start);
            else
                Start();

            if (!tcs.Task.Wait(timeout))
            {
                _lastShotFailDetail = $"截屏超时 display={displayId}";
                AppLog.Warn(_lastShotFailDetail);
                return null;
            }

            return tcs.Task.Result;
        }
        catch (Exception ex)
        {
            _lastShotFailDetail = "TakeScreenshot 异常: " + ex.Message;
            Log.Warn("FlyOcr", _lastShotFailDetail);
            return null;
        }
        finally
        {
            if (ReferenceEquals(_pinnedShotCallback, callback))
                _pinnedShotCallback = null;
            try { executor?.Shutdown(); } catch { /* ignore */ }
        }
    }

    private static string? RecognizeText(Bitmap bitmap, int rotationDegrees = 0)
    {
        InputImage? image = null;
        try
        {
            var recognizer = GetRecognizer();
            var rot = rotationDegrees % 360;
            if (rot < 0) rot += 360;
            image = InputImage.FromBitmap(bitmap, rot);
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
            try { Console.WriteLine("OCR ML Kit failed: " + ex.Message); } catch { /* ignore */ }
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
        private readonly Action<string> _onFailDetail;

        public ShotCallback(TaskCompletionSource<Bitmap?> tcs, Action<string> onFailDetail)
        {
            _tcs = tcs;
            _onFailDetail = onFailDetail;
        }

        public void OnSuccess(AccessibilityService.ScreenshotResult? screenshot)
        {
            if (screenshot == null)
            {
                _onFailDetail("截屏结果为空");
                _tcs.TrySetResult(null);
                return;
            }

            HardwareBuffer? buffer = null;
            try
            {
                buffer = screenshot.HardwareBuffer;
                if (buffer == null)
                {
                    _onFailDetail("HardwareBuffer 为空");
                    _tcs.TrySetResult(null);
                    return;
                }

                using var hwBmp = Bitmap.WrapHardwareBuffer(buffer, screenshot.ColorSpace);
                if (hwBmp == null)
                {
                    _onFailDetail("WrapHardwareBuffer 失败");
                    _tcs.TrySetResult(null);
                    return;
                }

                // 部分机型 Copy(Argb8888) 失败，再试硬件配置直接用 / RGB_565
                var soft = hwBmp.Copy(Bitmap.Config.Argb8888!, false)
                           ?? hwBmp.Copy(Bitmap.Config.Rgb565!, false);
                if (soft == null)
                {
                    _onFailDetail("Bitmap.Copy 失败");
                    _tcs.TrySetResult(null);
                    return;
                }

                _tcs.TrySetResult(soft);
            }
            catch (Exception ex)
            {
                _onFailDetail("截屏位图转换失败: " + ex.Message);
                _tcs.TrySetResult(null);
            }
            finally
            {
                try { buffer?.Close(); } catch { /* ignore */ }
            }
        }

        public void OnFailure(int errorCode)
        {
            // 1=内部 2=无权限 3=间隔过短 4=无效显示 5=无效窗口 6=安全窗口
            var hint = errorCode switch
            {
                1 => "内部错误",
                2 => "无障碍无截屏权限（请关掉再开无障碍）",
                3 => "间隔过短",
                4 => "无效显示 ID",
                5 => "无效窗口",
                6 => "目标应用禁止截屏(FLAG_SECURE)",
                _ => "系统码 " + errorCode
            };
            var detail = $"截屏失败 code={errorCode}（{hint}）";
            _onFailDetail(detail);

            var now = System.Environment.TickCount64;
            if (now - _lastShotFailLogMs > 1000)
            {
                _lastShotFailLogMs = now;
                Log.Warn("FlyOcr", "TakeScreenshot OnFailure " + detail);
                try { AppLog.Warn("OCR" + detail); } catch { /* ignore */ }
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
