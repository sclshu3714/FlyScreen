namespace FlyScreen.Services.Update;

public sealed class UpdateDownloadProgress
{
    public double Percent { get; init; }
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; } = -1;
    public string Message { get; init; } = "";
    public bool IsIndeterminate => Percent < 0;

    public static UpdateDownloadProgress Indeterminate(string message) => new()
    {
        Percent = -1,
        Message = message
    };

    public static UpdateDownloadProgress Complete(string message) => new()
    {
        Percent = 100,
        Message = message
    };
}

public sealed class UpdateCheckResult
{
    public bool Success { get; init; }
    public bool UpdateAvailable { get; init; }
    public string LocalVersion { get; init; } = "";
    public string RemoteVersion { get; init; } = "";
    public string TagName { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public string? AssetName { get; init; }
    public string? AssetDownloadUrl { get; init; }
    public string Message { get; init; } = "";

    public static UpdateCheckResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}

public sealed class UpdateApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool ShouldExit { get; init; }

    public static UpdateApplyResult Ok(string message, bool shouldExit = true) => new()
    {
        Success = true,
        ShouldExit = shouldExit,
        Message = message
    };

    public static UpdateApplyResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}
