using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using FlyScreen.DesktopUi.Helpers;

namespace FlyScreen.DesktopUi.Editor;

/// <summary>AvaloniaEdit 左侧断点栏。</summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private static readonly IBrush DotBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    // 浅 #F3F4F6 / 深 #1E293B，与 ProgramEditorTheme.GutterBackgroundHex 一致
    private static readonly IBrush LightBg = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
    private static readonly IBrush DarkBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));

    private readonly Func<IReadOnlyCollection<int>> _getBreakpoints;
    private readonly Action<int> _toggle;

    public BreakpointMargin(Func<IReadOnlyCollection<int>> getBreakpoints, Action<int> toggle)
    {
        _getBreakpoints = getBreakpoints;
        _toggle = toggle;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "点击添加/取消断点");
    }

    private static IBrush CurrentBgBrush()
        => DesktopThemeService.IsDark(DesktopThemeService.Current) ? DarkBg : LightBg;

    protected override Size MeasureOverride(Size availableSize)
        => new(18, 0);

    public override void Render(DrawingContext context)
    {
        var textView = TextView;
        if (textView == null || !textView.VisualLinesValid)
            return;

        context.FillRectangle(CurrentBgBrush(), new Rect(Bounds.Size));
        var bps = _getBreakpoints();
        foreach (var line in textView.VisualLines)
        {
            var num = line.FirstDocumentLine.LineNumber;
            if (!bps.Contains(num)) continue;
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextMiddle)
                    - textView.VerticalOffset;
            context.DrawEllipse(DotBrush, null, new Point(Bounds.Width / 2, y), 5, 5);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        e.Handled = true;
        var textView = TextView;
        if (textView == null || !textView.VisualLinesValid) return;

        var pos = e.GetPosition(textView);
        var y = pos.Y + textView.VerticalOffset;
        var vl = textView.GetVisualLineFromVisualTop(y);
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
