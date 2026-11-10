using FlyScreen.Services;
using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.SharedDevices;

/// <summary>与 ToolSdk 同值，供工具 UI 程序集沿用短名。</summary>
public static class SharedDeviceKinds
{
    public const string Speaker = global::FlyScreen.ToolSdk.SharedDeviceKinds.Speaker;
    public const string Microphone = global::FlyScreen.ToolSdk.SharedDeviceKinds.Microphone;
    public const string Camera = global::FlyScreen.ToolSdk.SharedDeviceKinds.Camera;
    public static readonly string[] All = global::FlyScreen.ToolSdk.SharedDeviceKinds.All;
    public static bool IsValid(string? kind) => global::FlyScreen.ToolSdk.SharedDeviceKinds.IsValid(kind);
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
