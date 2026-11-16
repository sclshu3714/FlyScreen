namespace FlyScreen.Tool.VideoWall.Contracts;

/// <summary>
/// 软件帧接收面：LibVLC 内存回调出画。
/// 各端<strong>单路播放器</strong>用此面把画面嵌进 UI 树（与 chrome 同生命周期），避免 VideoView ForegroundWindow 等顶层浮层。
/// 墙格多路仍可走 HWND。回调可能在解码线程触发；实现方应尽快拷贝。
/// </summary>
public interface IVideoFrameSink
{
    /// <summary>输出格式确定（宽高/行跨度）；可能多次调用（分辨率变化）。</summary>
    void OnVideoFormat(int width, int height, int strideBytes);

    /// <summary>
    /// 一帧写满。<paramref name="scan0"/> 仅在本次调用返回前有效。
    /// </summary>
    void OnVideoFrame(IntPtr scan0, int width, int height, int strideBytes);
}
