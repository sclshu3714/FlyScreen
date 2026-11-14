namespace FlyScreen.Tool.SharedDevices.Ui.Maui;

/// <summary>
/// 共享设备页响应式宽度：手机竖屏铺满；宽屏居中固定宽壳，文案变化不得改宽。
/// </summary>
internal static class SharedDevicesMauiLayout
{
    public const double DesktopCardWidth = 480;

    public static void Apply(Grid host, View shell, double width, double height)
    {
        if (width <= 0 || double.IsNaN(width)) return;

        var landscape = width > height;
        var phone = DeviceInfo.Idiom == DeviceIdiom.Phone;
        var desktop = DeviceInfo.Idiom == DeviceIdiom.Desktop || width >= 900;

        // 手机竖屏：壳铺满，不锁宽
        if (phone && !landscape)
        {
            host.Padding = new Thickness(12, 14);
            ClearFixedWidth(shell);
            shell.MaximumWidthRequest = double.PositiveInfinity;
            shell.HorizontalOptions = LayoutOptions.Fill;
            return;
        }

        // 固定像素宽：Minimum=Maximum=WidthRequest，选项切换也不能再测宽撑开
        double target = desktop ? DesktopCardWidth : phone ? 420 : 460;
        target = Math.Min(target, Math.Max(280, width - (phone ? 40 : 96)));
        host.Padding = new Thickness(
            phone ? 20 : desktop ? 32 : 24,
            phone ? (landscape ? 10 : 14) : 24);
        shell.WidthRequest = target;
        shell.MinimumWidthRequest = target;
        shell.MaximumWidthRequest = target;
        shell.HorizontalOptions = LayoutOptions.Center;
    }

    private static void ClearFixedWidth(View view)
    {
        view.WidthRequest = -1;
        view.MinimumWidthRequest = -1;
    }
}
