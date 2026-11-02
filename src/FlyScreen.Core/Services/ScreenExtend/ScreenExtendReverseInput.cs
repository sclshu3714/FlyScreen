using FlyScreen.Services.Network;

namespace FlyScreen.Services.ScreenExtend;

/// <summary>从机播放器把键鼠打成 ScreenExtendInput 发给主机。</summary>
public static class ScreenExtendReverseInput
{
    public static Task SendMoveAsync(
        ConnectionManager connections, string peer, Guid sessionId, double nx, double ny)
        => SendAsync(connections, peer, new ScreenExtendInputPayload
        {
            SessionId = sessionId.ToString("D"),
            Kind = ScreenExtendInputPayload.KindMove,
            Nx = Clamp01(nx),
            Ny = Clamp01(ny)
        });

    public static Task SendButtonAsync(
        ConnectionManager connections, string peer, Guid sessionId,
        double nx, double ny, int button, bool down)
        => SendAsync(connections, peer, new ScreenExtendInputPayload
        {
            SessionId = sessionId.ToString("D"),
            Kind = ScreenExtendInputPayload.KindButton,
            Nx = Clamp01(nx),
            Ny = Clamp01(ny),
            Button = button,
            Down = down
        });

    public static Task SendWheelAsync(
        ConnectionManager connections, string peer, Guid sessionId,
        double nx, double ny, int delta, bool horizontal)
        => SendAsync(connections, peer, new ScreenExtendInputPayload
        {
            SessionId = sessionId.ToString("D"),
            Kind = ScreenExtendInputPayload.KindWheel,
            Nx = Clamp01(nx),
            Ny = Clamp01(ny),
            Delta = delta,
            Horizontal = horizontal
        });

    public static Task SendKeyAsync(
        ConnectionManager connections, string peer, Guid sessionId,
        ushort virtualKey, bool down, bool extended = false, string? text = null)
        => SendAsync(connections, peer, new ScreenExtendInputPayload
        {
            SessionId = sessionId.ToString("D"),
            Kind = ScreenExtendInputPayload.KindKey,
            VirtualKey = virtualKey,
            Down = down,
            Extended = extended,
            Text = text
        });

    /// <summary>
    /// 把播放器客户区上的点映射到 Uniform 画面内容区的 0..1。
    /// 黑边（letterbox）上的点返回 false。
    /// </summary>
    public static bool TryMapUniformContent(
        double viewWidth, double viewHeight,
        double contentWidth, double contentHeight,
        double localX, double localY,
        out double nx, out double ny)
    {
        nx = ny = 0;
        if (viewWidth <= 1 || viewHeight <= 1 || contentWidth <= 0 || contentHeight <= 0)
            return false;

        var scale = Math.Min(viewWidth / contentWidth, viewHeight / contentHeight);
        var drawW = contentWidth * scale;
        var drawH = contentHeight * scale;
        var ox = (viewWidth - drawW) * 0.5;
        var oy = (viewHeight - drawH) * 0.5;
        if (localX < ox || localY < oy || localX > ox + drawW || localY > oy + drawH)
            return false;

        nx = Clamp01((localX - ox) / Math.Max(1e-6, drawW - 1e-6));
        ny = Clamp01((localY - oy) / Math.Max(1e-6, drawH - 1e-6));
        return true;
    }

    private static async Task SendAsync(
        ConnectionManager connections, string peer, ScreenExtendInputPayload payload)
    {
        if (connections == null || string.IsNullOrWhiteSpace(peer)) return;
        try
        {
            await connections.SendToAsync(peer, MessageType.ScreenExtendInput,
                MessageCodec.JsonBytes(payload)).ConfigureAwait(false);
        }
        catch
        {
            // 回控丢包不拆会话
        }
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
}
