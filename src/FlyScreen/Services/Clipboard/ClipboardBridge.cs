using System.Collections.Specialized;
using System.Windows;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 线程安全剪贴板读写，并通过抑制标志避免回环广播。
/// </summary>
public static class ClipboardBridge
{
    private static int _suppress;

    public static bool IsSuppressed => Volatile.Read(ref _suppress) > 0;

    public static void SetTextSuppressing(string text)
    {
        Interlocked.Increment(ref _suppress);
        try
        {
            System.Windows.Clipboard.Clear();
            System.Windows.Clipboard.SetText(text);
        }
        finally
        {
            // 延迟解除，避开监听器回调
            _ = Task.Run(async () =>
            {
                await Task.Delay(400).ConfigureAwait(false);
                Interlocked.Decrement(ref _suppress);
            });
        }
    }

    public static void SetFilesSuppressing(IReadOnlyList<string> files)
    {
        Interlocked.Increment(ref _suppress);
        try
        {
            var col = new StringCollection();
            foreach (var f in files)
                col.Add(f);
            System.Windows.Clipboard.Clear();
            System.Windows.Clipboard.SetFileDropList(col);
        }
        finally
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(400).ConfigureAwait(false);
                Interlocked.Decrement(ref _suppress);
            });
        }
    }

    public static string? TryGetText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static string[]? TryGetFiles()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsFileDropList()) return null;
            var list = System.Windows.Clipboard.GetFileDropList();
            var arr = new string[list.Count];
            list.CopyTo(arr, 0);
            return arr;
        }
        catch
        {
            return null;
        }
    }
}
