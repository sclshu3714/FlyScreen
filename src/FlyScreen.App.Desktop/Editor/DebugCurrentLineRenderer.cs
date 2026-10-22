using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace FlyScreen.DesktopUi.Editor;

/// <summary>调试当前执行行背景高亮（对齐 VS）。</summary>
public sealed class DebugCurrentLineRenderer : IBackgroundRenderer
{
    private static readonly IBrush LightBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFB, 0xC2));
    private static readonly IBrush DarkBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x4A, 0x1E));

    public KnownLayer Layer => KnownLayer.Background;

    public int? Line { get; set; }

    public bool DarkTheme { get; set; }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Line is not int line || line < 1 || textView?.Document == null)
            return;
        if (line > textView.Document.LineCount)
            return;

        double y;
        double height;
        var visualLine = textView.GetVisualLine(line);
        if (visualLine != null)
        {
            y = visualLine.VisualTop - textView.VerticalOffset;
            height = visualLine.Height;
        }
        else
        {
            height = textView.DefaultLineHeight > 0 ? textView.DefaultLineHeight : 16;
            y = (line - 1) * height - textView.VerticalOffset;
        }

        var brush = DarkTheme ? DarkBrush : LightBrush;
        drawingContext.FillRectangle(
            brush,
            new Rect(0, y, Math.Max(textView.Bounds.Width, 1), height));
    }
}
