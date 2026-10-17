#if ANDROID
using Android.Graphics;
using AView = global::Android.Views.View;
using ACanvas = global::Android.Graphics.Canvas;
using APath = global::Android.Graphics.Path;
using APaint = global::Android.Graphics.Paint;
using AColor = global::Android.Graphics.Color;
using ABitmap = global::Android.Graphics.Bitmap;
using PaintStyle = global::Android.Graphics.Paint.Style;
#endif

namespace FlyScreen.AppMaui.Services;

/// <summary>
/// 跨屏鼠标的应用内箭头。注入的触控在 Android 上被当成手指，系统不会画鼠标。
/// .NET 10 的 <see cref="Window"/> 没有可挂控件的 Overlay，这里在当前 Activity 最上层放一个全屏 View：
/// 不接收触摸（<see cref="AView.Clickable"/> 为 false，<c>OnTouchEvent</c> 返回 false），手指仍点到下面的页面。
/// </summary>
public static class VirtualCursorOverlay
{
    private static readonly object Gate = new();
    private static double _nx = 0.5;
    private static double _ny = 0.5;
    private static bool _visible;
#if ANDROID
    private static CursorView? _view;
#endif

    public static void Attach(Microsoft.Maui.Controls.Window window)
    {
#if ANDROID
        window.Created += (_, _) => MainThread.BeginInvokeOnMainThread(Ensure);
        window.HandlerChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Ensure);
        MainThread.BeginInvokeOnMainThread(Ensure);
#else
        _ = window;
#endif
    }

    public static void ShowNormalized(double nx, double ny)
    {
        lock (Gate)
        {
            _nx = Math.Clamp(nx, 0, 1);
            _ny = Math.Clamp(ny, 0, 1);
            _visible = true;
        }
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Ensure();
            _view?.BringToFront();
            _view?.Invalidate();
        });
#endif
    }

    public static void MoveByPixels(int dx, int dy)
    {
        var info = DeviceDisplay.MainDisplayInfo;
        var w = Math.Max(info.Width, 1);
        var h = Math.Max(info.Height, 1);
        lock (Gate)
        {
            _nx = Math.Clamp(_nx + dx / w, 0, 1);
            _ny = Math.Clamp(_ny + dy / h, 0, 1);
            _visible = true;
        }
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Ensure();
            _view?.Invalidate();
        });
#endif
    }

    public static void Hide()
    {
        lock (Gate)
            _visible = false;
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() => _view?.Invalidate());
#endif
    }

#if ANDROID
    private static void Ensure()
    {
        if (_view?.Parent != null) return;
        var activity = Platform.CurrentActivity;
        if (activity?.Window?.DecorView is not global::Android.Views.ViewGroup decor) return;

        _view = new CursorView(activity);
        decor.AddView(_view, new global::Android.Views.ViewGroup.LayoutParams(
            global::Android.Views.ViewGroup.LayoutParams.MatchParent,
            global::Android.Views.ViewGroup.LayoutParams.MatchParent));
        _view.LoadCursor();
    }

    private static bool TryGet(out double nx, out double ny)
    {
        lock (Gate)
        {
            nx = _nx;
            ny = _ny;
            return _visible;
        }
    }

    private sealed class CursorView : AView
    {
        private readonly APaint _fill;
        private readonly APaint _stroke;
        private ABitmap? _bitmap;

        public CursorView(global::Android.Content.Context context) : base(context)
        {
            Clickable = false;
            LongClickable = false;
            Focusable = false;
            FocusableInTouchMode = false;
            SetWillNotDraw(false);
            SetBackgroundColor(AColor.Transparent);
            _fill = new APaint(PaintFlags.AntiAlias) { Color = AColor.White };
            _fill.SetStyle(PaintStyle.Fill);
            _stroke = new APaint(PaintFlags.AntiAlias)
            {
                Color = AColor.Black,
                StrokeWidth = 0.55f
            };
            _stroke.SetStyle(PaintStyle.Stroke);
            _stroke.StrokeJoin = global::Android.Graphics.Paint.Join.Round;
        }

        public override bool DispatchTouchEvent(global::Android.Views.MotionEvent? e) => false;

        public override bool OnTouchEvent(global::Android.Views.MotionEvent? e) => false;

        public void LoadCursor()
        {
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            try
            {
                await using var stream = await FileSystem.OpenAppPackageFileAsync("cursor.png").ConfigureAwait(false);
                var bitmap = await BitmapFactory.DecodeStreamAsync(stream).ConfigureAwait(false);
                if (bitmap == null) return;
                _bitmap = bitmap;
                PostInvalidate();
            }
            catch
            {
                TryLoadDrawable();
            }
        }

        private void TryLoadDrawable()
        {
            var context = Context;
            if (context == null) return;
            var id = context.Resources?.GetIdentifier("cursor", "drawable", context.PackageName) ?? 0;
            if (id == 0) return;
            _bitmap = BitmapFactory.DecodeResource(context.Resources, id);
            PostInvalidate();
        }

        protected override void OnDraw(ACanvas canvas)
        {
            base.OnDraw(canvas);
            if (!TryGet(out var nx, out var ny)) return;
            if (Width < 2 || Height < 2) return;

            var density = Resources?.DisplayMetrics?.Density ?? 2f;
            var size = 28f * density;
            var x = (float)(nx * Math.Max(Width - 1, 1));
            var y = (float)(ny * Math.Max(Height - 1, 1));
            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(size / 32f, size / 32f);
            using var path = new APath();
            path.MoveTo(1, 1);
            path.LineTo(1, 26);
            path.LineTo(7, 20);
            path.LineTo(12, 30);
            path.LineTo(17, 28);
            path.LineTo(12, 18);
            path.LineTo(20, 18);
            path.Close();
            canvas.DrawPath(path, _fill);
            canvas.DrawPath(path, _stroke);
            canvas.Restore();
        }
    }
#endif
}
