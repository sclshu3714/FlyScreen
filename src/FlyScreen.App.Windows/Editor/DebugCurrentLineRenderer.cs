using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlyScreen.Editor;

/// <summary>调试当前执行行背景高亮（对齐 VS）。</summary>
public sealed class DebugCurrentLineRenderer : IBackgroundRenderer
{
    private static readonly SolidColorBrush LightBrush = Freeze(0xFF, 0xFB, 0xC2);
    private static readonly SolidColorBrush DarkBrush = Freeze(0x5B, 0x4A, 0x1E);

    public KnownLayer Layer => KnownLayer.Background;

    public int? Line { get; set; }

    public bool DarkTheme { get; set; }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Line is not int line || line < 1 || textView?.Document == null)
            return;
        if (line > textView.Document.LineCount)
            return;

        // 优先用已构造的 VisualLine；滚动后尚未构造时用文档行高估算，避免黄底「不生效」
        double y;
        double height;
        var visualLine = textView.GetVisualLine(line);
        if (visualLine != null)
        {
            y = visualLine.VisualTop - textView.ScrollOffset.Y;
            height = visualLine.Height;
        }
        else
        {
            height = textView.DefaultLineHeight > 0 ? textView.DefaultLineHeight : 16;
            y = (line - 1) * height - textView.VerticalOffset;
        }

        var brush = DarkTheme ? DarkBrush : LightBrush;
        drawingContext.DrawRectangle(
            brush, null,
            new Rect(0, y, Math.Max(textView.ActualWidth, 1), height));
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
