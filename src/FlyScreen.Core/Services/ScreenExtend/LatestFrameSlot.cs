namespace FlyScreen.Services.ScreenExtend;

/// <summary>
/// 接收端只保留最新一帧。UI 还在画上一帧时，后到的帧覆盖槽位，不排队解码。
/// 多 Tab 时带 peer，解码后投到对应标签。
/// </summary>
public sealed class LatestFrameSlot
{
    private readonly object _gate = new();
    private byte[]? _jpeg;
    private int _width;
    private int _height;
    private long _capturedUnixMs;
    private string _peer = "";
    private int _gen;
    private bool _posted;

    /// <summary>写入最新帧。返回 true 时调用方需要安排一次 UI 呈现。</summary>
    public bool Offer(ReadOnlyMemory<byte> jpeg, int width, int height, long capturedUnixMs = 0,
        string peer = "")
    {
        if (jpeg.Length == 0 || width <= 0 || height <= 0) return false;
        lock (_gate)
        {
            _jpeg = jpeg.ToArray();
            _width = width;
            _height = height;
            _capturedUnixMs = capturedUnixMs;
            _peer = peer ?? "";
            _gen++;
            if (_posted) return false;
            _posted = true;
            return true;
        }
    }

    /// <summary>取出当前最新帧。没有待显示的帧时返回 false。</summary>
    public bool TryTake(out byte[] jpeg, out int width, out int height, out int generation)
        => TryTake(out jpeg, out width, out height, out _, out generation, out _);

    public bool TryTake(out byte[] jpeg, out int width, out int height, out long capturedUnixMs,
        out int generation)
        => TryTake(out jpeg, out width, out height, out capturedUnixMs, out generation, out _);

    public bool TryTake(out byte[] jpeg, out int width, out int height, out long capturedUnixMs,
        out int generation, out string peer)
    {
        lock (_gate)
        {
            _posted = false;
            if (_jpeg == null)
            {
                jpeg = [];
                width = 0;
                height = 0;
                capturedUnixMs = 0;
                generation = 0;
                peer = "";
                return false;
            }

            jpeg = _jpeg;
            width = _width;
            height = _height;
            capturedUnixMs = _capturedUnixMs;
            generation = _gen;
            peer = _peer;
            _jpeg = null;
            return true;
        }
    }

    /// <summary>槽里是否还有还没取走的帧。</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate) return _jpeg != null;
        }
    }

    /// <summary>异步解码结束后，这一代是否仍是最新帧。</summary>
    public bool IsCurrent(int generation)
    {
        lock (_gate) return generation == _gen;
    }

    /// <summary>会话结束：丢掉待显示帧，并让进行中的解码结果失效。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _jpeg = null;
            _peer = "";
            _posted = false;
            _gen++;
        }
    }
}
