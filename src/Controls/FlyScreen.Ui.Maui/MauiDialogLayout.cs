namespace FlyScreen.Ui.Maui;

/// <summary>
/// MAUI 弹窗布局尺寸：供 <see cref="MauiDialogShell"/> 使用。
/// 手机窄屏/横屏紧凑边距；平板与电脑对齐 Win 对话框宽高。
/// </summary>
public static class MauiDialogLayout
{
    public const double CompactWidth = 600;
    public const double PhoneWidth = 480;

    public static bool IsCompact(double pageWidth)
        => pageWidth > 1 && pageWidth < CompactWidth
           || DeviceInfo.Idiom == DeviceIdiom.Phone;

    public static bool IsPhoneIdiom()
        => DeviceInfo.Idiom == DeviceIdiom.Phone;

    public static double FormCardMaxWidth(double pageWidth)
        => IsCompact(pageWidth) ? Math.Max(280, pageWidth - 32) : 440;

    public static double ContentMaxWidth(double pageWidth)
        => IsCompact(pageWidth) ? Math.Max(280, pageWidth - 24) : 720;

    public static Thickness PagePadding(double pageWidth, double pageHeight)
    {
        if (pageWidth < 420 || pageHeight < 420)
            return new Thickness(12);
        if (IsCompact(pageWidth))
            return new Thickness(16);
        return new Thickness(24);
    }
}
