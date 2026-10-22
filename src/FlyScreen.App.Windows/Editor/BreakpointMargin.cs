using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FlyScreen.Helpers;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlyScreen.Editor;

/// <summary>AvalonEdit 左侧断点栏：点击切换断点。</summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private static readonly SolidColorBrush DotBrush = CreateBrush(0xEF, 0x44, 0x44);
    // 浅 #F3F4F6 / 深 #1E293B，与 ProgramEditorTheme.GutterBackgroundHex 一致
    private static readonly SolidColorBrush LightBg = CreateBrush(0xF3, 0xF4, 0xF6);
    private static readonly SolidColorBrush DarkBg = CreateBrush(0x1E, 0x29, 0x3B);

    private static readonly SolidColorBrush ArrowBrush = CreateBrush(0xFA, 0xCC, 0x15);

    private readonly Func<IReadOnlyCollection<int>> _getBreakpoints;
    private readonly Action<int> _toggle;
    private int? _currentLine;

    public BreakpointMargin(Func<IReadOnlyCollection<int>> getBreakpoints, Action<int> toggle)
    {
        _getBreakpoints = getBreakpoints;
        _toggle = toggle;
        Cursor = System.Windows.Input.Cursors.Hand;
        ToolTip = "点击添加/取消断点";
    }

    /// <summary>调试当前执行行（黄三角）；null 清除。</summary>
    public void SetCurrentLine(int? line)
    {
        if (_currentLine == line) return;
        _currentLine = line;
        InvalidateVisual();
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CurrentBgBrush()
        => ThemeService.IsDark(ThemeService.Current) ? DarkBg : LightBg;

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        => new(18, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView == null || !textView.VisualLinesValid)
            return;

        drawingContext.DrawRectangle(CurrentBgBrush(), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var bps = _getBreakpoints();
        foreach (var line in textView.VisualLines)
        {
            var num = line.FirstDocumentLine.LineNumber;
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextMiddle)
                    - textView.VerticalOffset;
            if (bps.Contains(num))
                drawingContext.DrawEllipse(DotBrush, null, new Point(ActualWidth / 2, y), 5, 5);
            // 当前执行行：断点旁（或单独）画黄三角 ▶
            if (_currentLine == num)
                DrawCurrentArrow(drawingContext, y);
        }
    }

    private void DrawCurrentArrow(DrawingContext dc, double midY)
    {
        var tipX = ActualWidth - 2;
        var backX = 3;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(backX, midY - 5), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(tipX, midY), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(backX, midY + 5), isStroked: true, isSmoothJoin: false);
        }
        geo.Freeze();
        dc.DrawGeometry(ArrowBrush, null, geo);
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
