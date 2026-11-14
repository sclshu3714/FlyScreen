using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.SharedDevices;

/// <summary>共享设备文案（独立于主程序 Loc 表）。</summary>
public sealed class SharedDevicesLocalization : IToolLocalization
{
    public static SharedDevicesLocalization Instance { get; } = new();

    static SharedDevicesLocalization() => EnsureRegistered();

    public string SourceId => "shared-devices";

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; } = Build();

    public static void EnsureRegistered() => Loc.RegisterPlugin(Instance);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Build()
        => new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = Zh(),
            ["en-US"] = En()
        };

    private static Dictionary<string, string> Zh() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.SharedDevices.Name"] = "共享设备",
        ["Tool.SharedDevices.Summary"] = "向布局内已装本工具的设备申请扬声器、麦克风或摄像头（电脑/手机等均可）；对方同意后即可使用。",
        ["Tool.SharedDevices.StartTitle"] = "启动共享设备",
        ["Tool.SharedDevices.PickPeer"] = "选择目标设备",
        ["Tool.SharedDevices.PickKinds"] = "选择要申请的设备",
        ["Tool.SharedDevices.Speaker"] = "扬声器",
        ["Tool.SharedDevices.Microphone"] = "麦克风",
        ["Tool.SharedDevices.Camera"] = "摄像头",
        ["Tool.SharedDevices.Confirm"] = "确认启动",
        ["Tool.SharedDevices.Cancel"] = "取消",
        ["Tool.SharedDevices.IncomingTitle"] = "共享设备申请",
        ["Tool.SharedDevices.IncomingBody"] = "「{0}」申请使用本机：{1}",
        ["Tool.SharedDevices.Accept"] = "同意",
        ["Tool.SharedDevices.Reject"] = "拒绝",
        ["Tool.SharedDevices.Remember"] = "记住适配",
        ["Tool.SharedDevices.RememberHint"] = "勾选后，同一申请端再次申请相同种类时无需再同意；未勾选则每次都需同意。",
        ["Tool.SharedDevices.NoTargets"] = "布局内没有可申请的设备（需已连接、已安装共享设备工具，且具备扬声器/麦克风/摄像头之一）。",
        ["Tool.SharedDevices.ManageTitle"] = "共享设备管理",
        ["Tool.SharedDevices.PickLocalDevice"] = "选择本机设备",
        ["Tool.SharedDevices.NoLocalDevice"] = "未检测到可用设备",
        ["Tool.SharedDevices.DefaultDevice"] = "系统默认",
        ["Tool.SharedDevices.ClearRemember"] = "清除记住适配",
        ["Tool.SharedDevices.ClearRememberDone"] = "已清除记住适配记录",
        ["Tool.SharedDevices.Apply"] = "应用",
        ["Tool.SharedDevices.Save"] = "保存",
        ["Tool.SharedDevices.PeerNeedTool"] = "未安装工具",
        ["Tool.SharedDevices.PeerOffline"] = "未连接",
        ["Tool.SharedDevices.PeerNotDesktop"] = "非电脑",
        ["Tool.SharedDevices.PeerNoDevice"] = "无设备",
        ["Tool.SharedDevices.TestTitle"] = "共享设备测试",
        ["Tool.SharedDevices.TestCamera"] = "测试摄像头",
        ["Tool.SharedDevices.TestCameraHint"] = "选择本机摄像头并开始预览，确认画面正常。",
        ["Tool.SharedDevices.TestCameraIdle"] = "尚未开始预览",
        ["Tool.SharedDevices.TestCameraFail"] = "无法打开摄像头预览",
        ["Tool.SharedDevices.TestNoCamera"] = "未检测到摄像头",
        ["Tool.SharedDevices.TestNoCameraHint"] = "本机未枚举到摄像头驱动。",
        ["Tool.SharedDevices.TestSpeaker"] = "测试扬声器",
        ["Tool.SharedDevices.TestSpeakerHint"] = "播放短促测试音，确认本机扬声器可出声。",
        ["Tool.SharedDevices.TestPlayTone"] = "播放测试音",
        ["Tool.SharedDevices.TestSpeakerPlaying"] = "正在播放…",
        ["Tool.SharedDevices.TestSpeakerDone"] = "播放完成",
        ["Tool.SharedDevices.TestMicrophone"] = "测试麦克风",
        ["Tool.SharedDevices.TestMicHint"] = "对着麦克风说话，观察电平条是否跳动。",
        ["Tool.SharedDevices.TestMicListening"] = "正在监听…",
        ["Tool.SharedDevices.TestMicFail"] = "无法打开麦克风",
        ["Tool.SharedDevices.TestStart"] = "开始",
        ["Tool.SharedDevices.TestStop"] = "停止",
        ["Tool.SharedDevices.TestClose"] = "关闭",
        ["Tool.SharedDevices.TestNeedSession"] = "请先「开始」共享设备并等待对端同意，再测试对端设备。",
        ["Tool.SharedDevices.TestGoStart"] = "去启动共享…",
        ["Tool.SharedDevices.TestWaitingAccept"] = "已向「{0}」申请，等待对端同意…",
        ["Tool.SharedDevices.TestKindNotInSession"] = "当前会话未包含此设备",
        ["Tool.SharedDevices.TestWaitingPeer"] = "等待对端画面…",
        ["Tool.SharedDevices.TestRemoteCameraHint"] = "预览「{0}」的摄像头画面（经共享会话）。",
        ["Tool.SharedDevices.TestRemoteSpeakerHint"] = "在「{0}」本机扬声器播放试音。",
        ["Tool.SharedDevices.TestRemoteMicHint"] = "监听「{0}」麦克风电平（对着手机说话）。"
    };

    private static Dictionary<string, string> En() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.SharedDevices.Name"] = "Shared devices",
        ["Tool.SharedDevices.Summary"] = "Request speaker, microphone, or camera from any layout device with the tool installed (PC or phone); use after the peer accepts.",
        ["Tool.SharedDevices.StartTitle"] = "Start shared devices",
        ["Tool.SharedDevices.PickPeer"] = "Choose target device",
        ["Tool.SharedDevices.PickKinds"] = "Devices to request",
        ["Tool.SharedDevices.Speaker"] = "Speaker",
        ["Tool.SharedDevices.Microphone"] = "Microphone",
        ["Tool.SharedDevices.Camera"] = "Camera",
        ["Tool.SharedDevices.Confirm"] = "Confirm",
        ["Tool.SharedDevices.Cancel"] = "Cancel",
        ["Tool.SharedDevices.IncomingTitle"] = "Shared device request",
        ["Tool.SharedDevices.IncomingBody"] = "「{0}」 requests: {1}",
        ["Tool.SharedDevices.Accept"] = "Accept",
        ["Tool.SharedDevices.Reject"] = "Reject",
        ["Tool.SharedDevices.Remember"] = "Remember this peer",
        ["Tool.SharedDevices.RememberHint"] = "If checked, the same peer requesting the same kinds will be accepted without prompting; otherwise every request needs approval.",
        ["Tool.SharedDevices.NoTargets"] = "No eligible device in the layout (must be connected, have the tool installed, and offer speaker/mic/camera).",
        ["Tool.SharedDevices.ManageTitle"] = "Manage shared devices",
        ["Tool.SharedDevices.PickLocalDevice"] = "Choose local device",
        ["Tool.SharedDevices.NoLocalDevice"] = "No device available",
        ["Tool.SharedDevices.DefaultDevice"] = "System default",
        ["Tool.SharedDevices.ClearRemember"] = "Clear remembered peers",
        ["Tool.SharedDevices.ClearRememberDone"] = "Remembered peers cleared",
        ["Tool.SharedDevices.Apply"] = "Apply",
        ["Tool.SharedDevices.Save"] = "Save",
        ["Tool.SharedDevices.PeerNeedTool"] = "Tool not installed",
        ["Tool.SharedDevices.PeerOffline"] = "Offline",
        ["Tool.SharedDevices.PeerNotDesktop"] = "Not a PC",
        ["Tool.SharedDevices.PeerNoDevice"] = "No device",
        ["Tool.SharedDevices.TestTitle"] = "Shared devices test",
        ["Tool.SharedDevices.TestCamera"] = "Test camera",
        ["Tool.SharedDevices.TestCameraHint"] = "Pick a camera and start preview to verify the picture.",
        ["Tool.SharedDevices.TestCameraIdle"] = "Preview not started",
        ["Tool.SharedDevices.TestCameraFail"] = "Could not open camera preview",
        ["Tool.SharedDevices.TestNoCamera"] = "No camera found",
        ["Tool.SharedDevices.TestNoCameraHint"] = "No camera driver was enumerated on this PC.",
        ["Tool.SharedDevices.TestSpeaker"] = "Test speaker",
        ["Tool.SharedDevices.TestSpeakerHint"] = "Play a short test tone to verify the speaker.",
        ["Tool.SharedDevices.TestPlayTone"] = "Play test tone",
        ["Tool.SharedDevices.TestSpeakerPlaying"] = "Playing…",
        ["Tool.SharedDevices.TestSpeakerDone"] = "Done",
        ["Tool.SharedDevices.TestMicrophone"] = "Test microphone",
        ["Tool.SharedDevices.TestMicHint"] = "Speak into the mic and watch the level meter.",
        ["Tool.SharedDevices.TestMicListening"] = "Listening…",
        ["Tool.SharedDevices.TestMicFail"] = "Could not open microphone",
        ["Tool.SharedDevices.TestStart"] = "Start",
        ["Tool.SharedDevices.TestStop"] = "Stop",
        ["Tool.SharedDevices.TestClose"] = "Close",
        ["Tool.SharedDevices.TestNeedSession"] = "Start a shared-devices session and wait for the peer to accept before testing.",
        ["Tool.SharedDevices.TestGoStart"] = "Start sharing…",
        ["Tool.SharedDevices.TestWaitingAccept"] = "Request sent to “{0}”, waiting for accept…",
        ["Tool.SharedDevices.TestKindNotInSession"] = "This device is not in the current session",
        ["Tool.SharedDevices.TestWaitingPeer"] = "Waiting for peer video…",
        ["Tool.SharedDevices.TestRemoteCameraHint"] = "Preview camera from “{0}” over the shared session.",
        ["Tool.SharedDevices.TestRemoteSpeakerHint"] = "Play a test tone on “{0}” speakers.",
        ["Tool.SharedDevices.TestRemoteMicHint"] = "Listen to mic level from “{0}” (speak into the phone)."
    };
}
