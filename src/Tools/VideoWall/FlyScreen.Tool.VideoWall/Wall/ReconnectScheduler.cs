namespace FlyScreen.Tool.VideoWall.Wall;

/// <summary>指数退避重连调度（1s → 30s，可配置最大次数）。</summary>
public sealed class ReconnectScheduler
{
    public const int DefaultMaxAttempts = 20;

    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private int _attempt;

    public ReconnectScheduler(int maxAttempts = DefaultMaxAttempts)
    {
        MaxAttempts = Math.Max(1, maxAttempts);
    }

    public int MaxAttempts { get; }

    public int Attempt => _attempt;

    public bool CanRetry => _attempt < MaxAttempts;

    public TimeSpan GetNextDelay()
    {
        var exp = Math.Min(_attempt, 5);
        var seconds = Math.Min(MaxDelay.TotalSeconds, MinDelay.TotalSeconds * Math.Pow(2, exp));
        return TimeSpan.FromSeconds(seconds);
    }

    public void RecordAttempt() => _attempt++;

    public void Reset() => _attempt = 0;
}