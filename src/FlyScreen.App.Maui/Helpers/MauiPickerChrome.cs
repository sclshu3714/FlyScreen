namespace FlyScreen.AppMaui.Helpers;

/// <summary>
/// MAUI Picker（WinUI ComboBox）下拉列表背景：自定义壁纸主题下须实心 PopupBg，避免选项透底。
/// </summary>
public static class MauiPickerChrome
{
    /// <summary>MAUI <c>PickerExtensions.UpdateBackground</c> 会写入的键；WinRT 字典对已存在键再赋值会 HR 失败。</summary>
    private static readonly string[] MauiBackgroundKeys =
    [
        "ComboBoxBackground",
        "ComboBoxBackgroundPointerOver",
        "ComboBoxBackgroundPressed",
        "ComboBoxBackgroundDisabled",
        "ComboBoxBackgroundUnfocused"
    ];

    private static readonly string[] DropDownBackgroundKeys =
    [
        "ComboBoxDropDownBackground",
        "ComboBoxDropDownBackgroundPointerOver",
        "ComboBoxDropDownBackgroundDisabled"
    ];

    /// <summary>在 MAUI MapBackground 之前调用：清掉旧键，避免主题切换 ThrowExceptionForHR。</summary>
    public static void PrepareBackgroundUpdate(object? platformView)
    {
#if WINDOWS
        if (platformView is not Microsoft.UI.Xaml.Controls.ComboBox combo)
            return;
        RemoveKeys(combo, MauiBackgroundKeys);
#endif
    }

    public static void Apply(object? platformView)
    {
#if WINDOWS
        if (platformView is not Microsoft.UI.Xaml.Controls.ComboBox combo)
            return;

        try
        {
            var brush = ResolvePopupBrush();
            // 只改下拉列表底。勿写 ComboBoxBackground（属 MAUI UpdateBackground）
            // 先 Remove 再赋：WinRT ResourceDictionary 对已有键再 Insert 会 HR 失败
            RemoveKeys(combo, DropDownBackgroundKeys);
            foreach (var key in DropDownBackgroundKeys)
                combo.Resources[key] = brush;
        }
        catch
        {
            // 主题级联刷新中偶发 WinRT 字典失败，忽略以免打断切主题
        }
#endif
    }

#if WINDOWS
    private static void RemoveKeys(Microsoft.UI.Xaml.Controls.ComboBox combo, IEnumerable<string> keys)
    {
        try
        {
            foreach (var key in keys)
            {
                if (combo.Resources.ContainsKey(key))
                    combo.Resources.Remove(key);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush ResolvePopupBrush()
    {
        byte r = 255, g = 255, b = 255;
        if (Application.Current?.Resources.TryGetValue("PopupBg", out var v) == true)
        {
            if (v is Color mc)
            {
                r = (byte)Math.Clamp((int)Math.Round(mc.Red * 255), 0, 255);
                g = (byte)Math.Clamp((int)Math.Round(mc.Green * 255), 0, 255);
                b = (byte)Math.Clamp((int)Math.Round(mc.Blue * 255), 0, 255);
            }
            else if (v is string s)
                TryParseRgb(s, out r, out g, out b);
        }

        // 始终不透明：自定义壁纸主题下半透明会透底
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }

    private static void TryParseRgb(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 255;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 8) h = h[2..];
        if (h.Length != 6) return;
        r = Convert.ToByte(h[..2], 16);
        g = Convert.ToByte(h[2..4], 16);
        b = Convert.ToByte(h[4..6], 16);
    }
#endif
}
