using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyScreen.Helpers;

/// <summary>
/// 把图标解码成普通位图。窗口直接引用 .ico 时，投影切换显示器 / DPI 会在解码缩略图时抛 InvalidCastException。
/// </summary>
internal static class AppIconImage
{
    private static readonly Uri PackUri = new("pack://application:,,,/Assets/wlogo_tu.ico");
    private static ImageSource? _cached;

    public static ImageSource? Load()
    {
        if (_cached != null) return _cached;
        try
        {
            var info = Application.GetResourceStream(PackUri);
            if (info?.Stream == null) return null;
            using var stream = info.Stream;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            using var icon = new Icon(copy, 32, 32);
            using var bmp = icon.ToBitmap();
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var source = BitmapSource.Create(
                    bmp.Width,
                    bmp.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    data.Scan0,
                    data.Stride * bmp.Height,
                    data.Stride);
                source.Freeze();
                _cached = source;
                return source;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
    }
}
