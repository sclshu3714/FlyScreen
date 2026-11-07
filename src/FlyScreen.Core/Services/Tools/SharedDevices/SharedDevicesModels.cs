using FlyScreen.Helpers.Localization;

namespace FlyScreen.Services.Tools.SharedDevices;

public static class SharedDeviceKinds
{
    public const string Speaker = "speaker";
    public const string Microphone = "microphone";
    public const string Camera = "camera";

    public static readonly string[] All = [Speaker, Microphone, Camera];

    public static bool IsValid(string? kind)
        => All.Any(k => k.Equals(kind, StringComparison.OrdinalIgnoreCase));
}

public static class SharedDevicesOps
{
    public const string Request = "request";
    public const string RequestAccept = "request-accept";
    public const string RequestReject = "request-reject";
    public const string SessionClose = "session-close";
    public const string MediaState = "media-state";
    /// <summary>申请端请求提供方开启本机测试采集/播放。</summary>
    public const string TestStart = "test-start";
    public const string TestStop = "test-stop";
}

public sealed class SharedDevicesTestBody
{
    public string Kind { get; set; } = "";
}

public sealed class SharedDevicesRequestBody
{
    public string[] Kinds { get; set; } = [];
}

public sealed class SharedDevicesAcceptBody
{
    public string[] Kinds { get; set; } = [];
}

public sealed class SharedDevicesRejectBody
{
    public string[]? Kinds { get; set; }
    public string Reason { get; set; } = "denied";
}

public sealed class SharedDevicesCloseBody
{
    public string[]? Kinds { get; set; }
    public string? Reason { get; set; }
}

/// <summary>启动面板可选目标。</summary>
public sealed class SharedDevicesTargetInfo
{
    public string PeerName { get; set; } = "";
    public bool ToolInstalled { get; set; }
    public bool Connected { get; set; }
    public bool IsDesktop { get; set; }
    /// <summary>对端可提供种类（未知时默认三种，由对端拒绝校正）。</summary>
    public List<string> ProvidableKinds { get; set; } = [.. SharedDeviceKinds.All];

    /// <summary>可选：已装工具、已连接、至少一种可提供设备（电脑/手机/平板均可）。</summary>
    public bool CanProvide => ToolInstalled && Connected && ProvidableKinds.Count > 0;

    public string FormatDisplayName()
    {
        string? tag = null;
        if (!ToolInstalled) tag = Loc.T("Tool.SharedDevices.PeerNeedTool");
        else if (!Connected) tag = Loc.T("Tool.SharedDevices.PeerOffline");
        else if (ProvidableKinds.Count == 0) tag = Loc.T("Tool.SharedDevices.PeerNoDevice");
        return tag == null ? PeerName : $"{PeerName} ({tag})";
    }
}

public sealed class SharedDevicesSession
{
    public string SessionId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string[] Kinds { get; set; } = [];
    public bool IsApplicant { get; set; }
    public SharedDevicesSessionPhase Phase { get; set; }
}

public enum SharedDevicesSessionPhase
{
    Requesting,
    Active,
    Closing
}

public sealed class SharedDevicesIncomingRequest
{
    public string FromPeer { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string[] Kinds { get; set; } = [];
}
