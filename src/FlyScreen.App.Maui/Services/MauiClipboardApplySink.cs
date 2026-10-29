using FlyScreen.Services.Clipboard;
#if WINDOWS
using FlyScreen.AppMaui.Platforms.Windows;
#endif
using MauiClipboard = Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard;

namespace FlyScreen.AppMaui.Services;

/// <summary>跨平台剪贴板写入。Windows 必须在 UI 线程；图片走 WinRT Bitmap。</summary>
public sealed class MauiClipboardApplySink : IClipboardApplySink
{
    private long _suppressUntilMs;
    private string? _lastAppliedKey;

    public void Suppress(int ms)
        => _suppressUntilMs = Environment.TickCount64 + Math.Max(0, ms);

    public bool IsSuppressed => Environment.TickCount64 < _suppressUntilMs;

    public bool IsSameAsLastApplied(ClipboardInlineData data)
        => _lastAppliedKey != null && _lastAppliedKey == data.BuildKey();

    public void ApplyText(string text)
    {
        if (string.IsNullOrEmpty(text) || IsSuppressed) return;
        Suppress(1500);
        _ = SetTextAsync(text);
    }

    public void ApplyInline(ClipboardInlineData data)
    {
        if (data.IsEmpty || IsSuppressed) return;
        _lastAppliedKey = data.BuildKey();
        Suppress(2500);
#if WINDOWS
        _ = ApplyInlineWindowsAsync(data);
#elif ANDROID
        _ = ApplyInlineAndroidAsync(data);
#else
        var text = PreferText(data);
        if (!string.IsNullOrEmpty(text))
            _ = SetTextAsync(text);
#endif
    }

    public async Task<string?> TryGetTextAsync()
    {
        try
        {
            // Windows 剪贴板 COM 要求 UI 线程；后台轮询须切回主线程
            if (MainThread.IsMainThread)
                return await ReadTextOnCurrentThreadAsync().ConfigureAwait(true);

            return await MainThread.InvokeOnMainThreadAsync(ReadTextOnCurrentThreadAsync)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string? PreferText(ClipboardInlineData data)
        => !string.IsNullOrEmpty(data.Text) ? data.Text
            : !string.IsNullOrEmpty(data.Html) ? data.Html
            : !string.IsNullOrEmpty(data.Csv) ? data.Csv
            : data.Rtf;

    private static async Task<string?> ReadTextOnCurrentThreadAsync()
    {
        try
        {
            if (!MauiClipboard.Default.HasText)
                return null;
            return await MauiClipboard.Default.GetTextAsync().ConfigureAwait(true);
        }
        catch
        {
            // 剪贴板被占用 / 格式不支持等
            return null;
        }
    }

    private static async Task SetTextAsync(string text)
    {
        try
        {
            if (MainThread.IsMainThread)
            {
                await WriteTextOnCurrentThreadAsync(text).ConfigureAwait(true);
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() => WriteTextOnCurrentThreadAsync(text))
                .ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task WriteTextOnCurrentThreadAsync(string text)
    {
        try
        {
            await MauiClipboard.Default.SetTextAsync(text).ConfigureAwait(true);
        }
        catch
        {
            // ignore contention
        }
    }

#if WINDOWS
    private static async Task ApplyInlineWindowsAsync(ClipboardInlineData data)
    {
        try
        {
            if (MainThread.IsMainThread)
            {
                await MauiWinClipboardInline.ApplyAsync(data).ConfigureAwait(true);
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() => MauiWinClipboardInline.ApplyAsync(data))
                .ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }
#endif

#if ANDROID
    private static async Task ApplyInlineAndroidAsync(ClipboardInlineData data)
    {
        try
        {
            if (MainThread.IsMainThread)
                await ApplyInlineAndroidOnUiAsync(data).ConfigureAwait(true);
            else
                await MainThread.InvokeOnMainThreadAsync(() => ApplyInlineAndroidOnUiAsync(data))
                    .ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private static Task ApplyInlineAndroidOnUiAsync(ClipboardInlineData data)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            if (ctx == null) return Task.CompletedTask;

            var cm = ctx.GetSystemService(global::Android.Content.Context.ClipboardService)
                as global::Android.Content.ClipboardManager;
            if (cm == null) return Task.CompletedTask;

            if (data.Png is { Length: > 0 })
            {
                var dir = Path.Combine(ctx.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "flyscreen-clip");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"clip-{Guid.NewGuid():N}.png");
                File.WriteAllBytes(path, data.Png);

                var file = new Java.IO.File(path);
                var authority = ctx.PackageName + ".fileprovider";
                global::Android.Net.Uri? uri = null;
                try
                {
                    uri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(ctx, authority, file);
                }
                catch
                {
                    uri = global::Android.Net.Uri.FromFile(file);
                }

                if (uri != null)
                {
                    var clip = global::Android.Content.ClipData.NewUri(ctx.ContentResolver, "image", uri);
                    if (!string.IsNullOrEmpty(data.Text))
                        clip.AddItem(new global::Android.Content.ClipData.Item(data.Text));
                    cm.PrimaryClip = clip;
                    return Task.CompletedTask;
                }
            }

            if (!string.IsNullOrEmpty(data.Html) && !string.IsNullOrEmpty(data.Text))
            {
                cm.PrimaryClip = global::Android.Content.ClipData.NewHtmlText("html", data.Text, data.Html);
                return Task.CompletedTask;
            }

            if (!string.IsNullOrEmpty(data.Html))
            {
                var plain = PreferText(data) ?? data.Html;
                cm.PrimaryClip = global::Android.Content.ClipData.NewHtmlText("html", plain, data.Html);
                return Task.CompletedTask;
            }

            var text = PreferText(data);
            if (!string.IsNullOrEmpty(text))
                cm.PrimaryClip = global::Android.Content.ClipData.NewPlainText("text", text);
        }
        catch
        {
            // fallback text
            var text = PreferText(data);
            if (!string.IsNullOrEmpty(text))
                return WriteTextOnCurrentThreadAsync(text!);
        }

        return Task.CompletedTask;
    }
#endif
}
