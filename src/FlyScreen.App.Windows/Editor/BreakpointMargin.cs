using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlyScreen.Editor;

/// <summary>AvalonEdit 左侧断点栏：点击切换断点。</summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private static readonly SolidColorBrush DotBrush = CreateBrush(0xEF, 0x44, 0x44);
    private static readonly SolidColorBrush BgBrush = CreateBrush(0xF3, 0xF4, 0xF6);

    private readonly Func<IReadOnlyCollection<int>> _getBreakpoints;
    private readonly Action<int> _toggle;

    public BreakpointMargin(Func<IReadOnlyCollection<int>> getBreakpoints, Action<int> toggle)
    {
        _getBreakpoints = getBreakpoints;
        _toggle = toggle;
        Cursor = System.Windows.Input.Cursors.Hand;
        ToolTip = "点击添加/取消断点";
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        => new(18, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView == null || !textView.VisualLinesValid)
            return;

        drawingContext.DrawRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var bps = _getBreakpoints();
        foreach (var line in textView.VisualLines)
        {
            var num = line.FirstDocumentLine.LineNumber;
            if (!bps.Contains(num)) continue;
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextMiddle)
                    - textView.VerticalOffset;
            drawingContext.DrawEllipse(DotBrush, null, new Point(ActualWidth / 2, y), 5, 5);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        var textView = TextView;
        if (textView == null || !textView.VisualLinesValid) return;

        var pos = e.GetPosition(textView);
        pos.X = 0;
        pos.Y += textView.VerticalOffset;
        var vl = textView.GetVisualLineFromVisualTop(pos.Y);
        if (vl == null) return;
        _toggle(vl.FirstDocumentLine.LineNumber);
        InvalidateVisual();
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null)
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null)
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e)
        => InvalidateVisual();
}
