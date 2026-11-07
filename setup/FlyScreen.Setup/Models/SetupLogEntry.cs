namespace FlyScreen.Setup.Models;

public enum SetupLogLevel
{
    Info,
    Warn,
    Error,
    Debug
}

public sealed class SetupLogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public SetupLogLevel Level { get; init; } = SetupLogLevel.Info;
    public string Message { get; init; } = "";

    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        SetupLogLevel.Error => "错误",
        SetupLogLevel.Warn => "警告",
        SetupLogLevel.Debug => "调试",
        _ => "信息"
    };

    public static SetupLogEntry Info(string message) => new()
    {
        Level = SetupLogLevel.Info,
        Message = message
    };

    public static SetupLogEntry Error(string message) => new()
    {
        Level = SetupLogLevel.Error,
        Message = message
    };

    public static SetupLogEntry Warn(string message) => new()
    {
        Level = SetupLogLevel.Warn,
        Message = message
    };

    /// <summary>根据文案粗判级别（含「错误」「失败」「取消」等）。</summary>
    public static SetupLogEntry FromMessage(string message)
    {
        var level = SetupLogLevel.Info;
        if (message.Contains("错误", StringComparison.Ordinal)
            || message.Contains("失败", StringComparison.Ordinal)
            || message.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            level = SetupLogLevel.Error;
        else if (message.Contains("取消", StringComparison.Ordinal)
                 || message.Contains("跳过", StringComparison.Ordinal)
                 || message.Contains("警告", StringComparison.Ordinal))
            level = SetupLogLevel.Warn;

        return new SetupLogEntry { Level = level, Message = message };
    }
}
