namespace FlyScreen.Tool.VideoWall.Contracts;

/// <summary>
/// 软件帧接收面：LibVLC 内存回调出画，供 MAUI 等无法与 HWND 叠层共存的宿主使用。
/// 回调可能在解码线程触发；实现方应尽快拷贝，勿长时间阻塞。
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
