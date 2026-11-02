namespace FlyScreen.Services.ScreenExtend;

/// <summary>投影帧内黑边的裁切矩形。坐标相对原帧左上角。</summary>
public readonly record struct ProjectionBlackBorder(int Left, int Top, int Width, int Height)
{
    public bool IsFullFrame(int frameW, int frameH) =>
        Left == 0 && Top == 0 && Width == frameW && Height == frameH;

    /// <summary>
    /// 把裁切后画面内的 0..1 映射回原帧 0..1（去黑边后的回控注入）。
    /// </summary>
    public void MapContentToFrame(
        double contentNx, double contentNy, int frameW, int frameH,
        out double frameNx, out double frameNy)
    {
        contentNx = Math.Clamp(contentNx, 0, 1);
        contentNy = Math.Clamp(contentNy, 0, 1);
        if (frameW <= 1 || frameH <= 1 || Width <= 0 || Height <= 0 || IsFullFrame(frameW, frameH))
        {
            frameNx = contentNx;
            frameNy = contentNy;
            return;
        }

        var px = Left + contentNx * Math.Max(0, Width - 1);
        var py = Top + contentNy * Math.Max(0, Height - 1);
        frameNx = Math.Clamp(px / Math.Max(1, frameW - 1), 0, 1);
        frameNy = Math.Clamp(py / Math.Max(1, frameH - 1), 0, 1);
    }

    /// <summary>
    /// 四边都有黑边时按测量值裁掉（虚拟屏把画面缩小后居中）。
    /// 只有一对边又宽又对称时裁掉这一对（播放器为了片源比例留下的黑边）。
    /// 单边细条（状态栏、导航条）不裁。
    /// </summary>
    public static ProjectionBlackBorder FromInsets(int frameW, int frameH, int left, int top, int right, int bottom)
    {
        if (frameW < 32 || frameH < 32)
            return new ProjectionBlackBorder(0, 0, Math.Max(frameW, 0), Math.Max(frameH, 0));

        left = Math.Clamp(left, 0, frameW);
        top = Math.Clamp(top, 0, frameH);
        right = Math.Clamp(right, 0, frameW - left);
        bottom = Math.Clamp(bottom, 0, frameH - top);

        var uniform = left > 2 && right > 2 && top > 2 && bottom > 2;
        if (!uniform)
        {
            (left, right) = LetterboxPair(left, right, frameW);
            (top, bottom) = LetterboxPair(top, bottom, frameH);
        }

        var cw = frameW - left - right;
        var ch = frameH - top - bottom;
        if (cw < 160 || ch < 160 || (left == 0 && top == 0 && right == 0 && bottom == 0))
            return new ProjectionBlackBorder(0, 0, frameW, frameH);

        return new ProjectionBlackBorder(left, top, cw, ch);
    }

    /// <summary>一对边都够宽、宽度接近，才当成播放器留白。取较窄的一边，避免切进画面。</summary>
    private static (int A, int B) LetterboxPair(int a, int b, int span)
    {
        var minBar = Math.Max(32, span / 12);
        if (a < minBar || b < minBar) return (0, 0);
        var diff = Math.Abs(a - b);
        var larger = Math.Max(a, b);
        if (diff > Math.Max(12, larger / 6)) return (0, 0);
        var n = Math.Min(a, b);
        return (n, n);
    }
}
