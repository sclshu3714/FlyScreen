#if ANDROID
using System.Text;
using Android.AccessibilityServices;
using Android.Graphics;
using Android.Hardware;
using Android.OS;
using Android.Util;
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

    /// <summary>
    /// 截取屏幕矩形并 OCR；失败 / 无文字 / API&lt;30 返回 <c>""</c>。
    /// 坐标为屏幕像素，对角顺序可颠倒。
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
        Bitmap? crop = null;
        try
        {
            full = CaptureScreenBitmap(TimeSpan.FromSeconds(4));
            if (full == null || full.Width <= 0 || full.Height <= 0)
                return "";

            var cl = Math.Clamp(l, 0, full.Width);
            var ct = Math.Clamp(t, 0, full.Height);
            var cr = Math.Clamp(r, 0, full.Width);
            var cb = Math.Clamp(b, 0, full.Height);
            if (cr <= cl || cb <= ct)
                return "";

            crop = Bitmap.CreateBitmap(full, cl, ct, cr - cl, cb - ct);
            if (crop == null)
                return "";

            return RecognizeText(crop) ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn("FlyOcr", "OcrRegion failed: " + ex.Message);
            return "";
        }
        finally
        {
            if (crop != null && !ReferenceEquals(crop, full))
            {
                try { crop.Recycle(); } catch { /* ignore */ }
            }
            if (full != null)
            {
                try { full.Recycle(); } catch { /* ignore */ }
            }
        }
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
            => _tcs.TrySetResult(null);
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
