using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace FlyScreen.Services.Network;

public enum MessageType : byte
{
    Handshake = 1,
    HandshakeAck = 2,
    Heartbeat = 3,
    HeartbeatAck = 4,
    MouseMove = 10,
    MouseButton = 11,
    MouseWheel = 12,
    KeyEvent = 20,
    ClipboardText = 30,
    FileOffer = 40,
    FileAccept = 41,
    FileChunk = 42,
    FileComplete = 43,
    FileAbort = 44,
    /// <summary>复制时仅广播文件元数据，不传内容。</summary>
    ClipboardFilesAnnounce = 45,
    /// <summary>粘贴机请求拉取；源端只向请求方发送。</summary>
    ClipboardFilesPull = 46,
    /// <summary>截图/图片：仅宣告元数据（兼容旧版）。</summary>
    ClipboardImageAnnounce = 47,
    /// <summary>粘贴机请求拉取图片（兼容旧版）。</summary>
    ClipboardImagePull = 48,
    /// <summary>图片分块（兼容旧版）。</summary>
    ClipboardImageChunk = 49,
    /// <summary>图片发送完毕（兼容旧版）。</summary>
    ClipboardImageComplete = 52,
    /// <summary>内联剪贴板（文本/HTML/RTF/CSV/图片）宣告。</summary>
    ClipboardInlineAnnounce = 53,
    /// <summary>粘贴机请求拉取内联剪贴板。</summary>
    ClipboardInlinePull = 54,
    /// <summary>内联剪贴板分块（8字节seq + 8字节offset + data）。</summary>
    ClipboardInlineChunk = 55,
    /// <summary>内联剪贴板发送完毕。</summary>
    ClipboardInlineComplete = 56,
    /// <summary>查询对端常用目录（桌面/文档/下载等）。</summary>
    RemoteFoldersQuery = 57,
    /// <summary>对端常用目录应答。</summary>
    RemoteFoldersReply = 58,
    SwitchFocus = 50,
    PeerInfo = 60,
    /// <summary>电脑布局同步（槽位 + 1×4/2×2）。</summary>
    LayoutSync = 61,
    /// <summary>当前应显示光标的电脑（其它机器隐藏光标）。</summary>
    CursorOwner = 62,
    /// <summary>高级「功能 / 行为」选项同步。</summary>
    BehaviorSync = 63,
    /// <summary>把本机程序发给已连接电脑；对端已有同名或相同 Id 则跳过。</summary>
    ProgramShare = 64,
    /// <summary>电脑→手机：跨屏中文组字状态（拼音 + 候选）。</summary>
    CompositionUpdate = 70,
    /// <summary>电脑→手机：取消组字浮层。</summary>
    CompositionCancel = 71,
    /// <summary>电脑→手机：已确认文本（插入目标）。</summary>
    TextInput = 72,
    /// <summary>手机→电脑：点选候选（Index 或直接 Text）。</summary>
    CompositionSelect = 73
}

public readonly struct NetMessage
{
    public MessageType Type { get; init; }
    public byte[] Payload { get; init; }

    public NetMessage(MessageType type, byte[]? payload = null)
    {
        Type = type;
        Payload = payload ?? [];
    }
}

public static class MessageCodec
{
    public const int HeaderSize = 5; // 1 type + 4 length
    public const int MaxPayload = 8 * 1024 * 1024; // 单帧最大 8MB（文件分块）

    public static byte[] Encode(MessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
            throw new InvalidOperationException("载荷过大");

        var buffer = new byte[HeaderSize + payload.Length];
        buffer[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), payload.Length);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static byte[] EncodeJson<T>(MessageType type, T obj)
        => Encode(type, JsonBytes(obj));

    public static byte[] JsonBytes<T>(T obj)
        => JsonSerializer.SerializeToUtf8Bytes(obj);

    public static T? DecodeJson<T>(ReadOnlySpan<byte> payload)
        => JsonSerializer.Deserialize<T>(payload);
}

public sealed class HandshakePayload
{
    public string MachineName { get; set; } = "";
    public string SecurityKey { get; set; } = "";
    public string LocalIp { get; set; } = "";
    /// <summary>1 = 现网 Win；≥2 携带 Platform / DeviceClass / Caps（缺省兼容 v1）。</summary>
    public int ProtocolVersion { get; set; } = 1;
    public bool IsReply { get; set; }
    /// <summary>发起方刚刚把对端加入布局，请对端在密钥一致且有空位时把本机加进布局。</summary>
    public bool RequestLayoutJoin { get; set; }
    /// <summary>拒绝原因：key=安全密钥不一致，full=对端布局已满。空表示接受。</summary>
    public string? RejectReason { get; set; }

    // --- ProtocolVersion ≥ 2 可选扩展（旧端忽略未知 JSON 字段）---
    public string? Platform { get; set; }
    public string? DeviceClass { get; set; }
    public string[]? Caps { get; set; }
    public string? AppVersion { get; set; }
}

/// <summary>能力位常量，见 docs/device-matrix.md。</summary>
public static class PeerCaps
{
    public const string EdgeMouse = "EdgeMouse";
    public const string AbsPointer = "AbsPointer";
    public const string ClipboardText = "ClipboardText";
    public const string ClipboardFiles = "ClipboardFiles";
    public const string DirectTransfer = "DirectTransfer";
    public const string LayoutSync = "LayoutSync";
    public const string InjectInput = "InjectInput";
    public const string CaptureInput = "CaptureInput";

    public static readonly string[] DesktopFull =
    [
        EdgeMouse, AbsPointer, ClipboardText, ClipboardFiles,
        DirectTransfer, LayoutSync, InjectInput, CaptureInput
    ];

    /// <summary>macOS 桌面：与 Win 对齐（键鼠 + 剪贴板文本/文件 + 直传 + 布局）。</summary>
    public static readonly string[] MacDesktopW1 =
    [
        EdgeMouse, AbsPointer, ClipboardText, ClipboardFiles,
        DirectTransfer, LayoutSync, InjectInput, CaptureInput
    ];

    /// <summary>Linux X11 桌面：与 Win 对齐。</summary>
    public static readonly string[] LinuxDesktopX11 =
    [
        EdgeMouse, AbsPointer, ClipboardText, ClipboardFiles,
        DirectTransfer, LayoutSync, InjectInput, CaptureInput
    ];

    /// <summary>Linux Wayland：无 EdgeMouse/捕获；剪贴板文本/文件 + 直传 + 有限注入。</summary>
    public static readonly string[] LinuxDesktopWayland =
    [
        AbsPointer, ClipboardText, ClipboardFiles, DirectTransfer, LayoutSync, InjectInput
    ];

    /// <summary>无图形会话：直传落盘 + 文本通道（无系统粘贴体验）；ClipboardFiles 表示可收文件到 Inbox。</summary>
    public static readonly string[] LinuxHeadless =
    [
        ClipboardText, ClipboardFiles, DirectTransfer
    ];

    /// <summary>
    /// Android 手机/平板：触控板控桌面 + 文本剪贴板 + 收文件；
    /// 另宣告 EdgeMouse/InjectInput，便于桌面边缘切入并由无障碍服务注入键鼠（须用户授权）。
    /// 不含 LayoutSync（不参与桌面布局同步主路径）。
    /// </summary>
    public static readonly string[] AndroidMobile =
    [
        EdgeMouse, AbsPointer, ClipboardText, DirectTransfer, InjectInput
    ];

    /// <summary>鸿蒙手机/平板（W5a）：对标 AndroidMobile。</summary>
    public static readonly string[] HarmonyMobile =
    [
        EdgeMouse, AbsPointer, ClipboardText, DirectTransfer, InjectInput
    ];

    /// <summary>鸿蒙电脑（W5b）：对标桌面；API 不足时由宿主降级去掉 EdgeMouse/Capture。</summary>
    public static readonly string[] HarmonyDesktop =
    [
        EdgeMouse, AbsPointer, ClipboardText, ClipboardFiles,
        DirectTransfer, LayoutSync, InjectInput, CaptureInput
    ];
}

public static class PeerPlatform
{
    public const string Windows = "Windows";
    public const string MacOS = "macOS";
    public const string Linux = "Linux";
    public const string Android = "Android";
    public const string HarmonyOS = "HarmonyOS";
}

public static class PeerDeviceClass
{
    public const string Desktop = "Desktop";
    public const string Phone = "Phone";
    public const string Tablet = "Tablet";
    public const string Tv = "Tv";
    public const string Unknown = "Unknown";
}

public sealed class MouseMovePayload
{
    public double Nx { get; set; } // 0..1 归一化
    public double Ny { get; set; }
    public bool Relative { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
}

public sealed class MouseButtonPayload
{
    public int Button { get; set; } // 0 left 1 right 2 middle
    public bool Down { get; set; }
}

public sealed class MouseWheelPayload
{
    public int Delta { get; set; }
    public bool Horizontal { get; set; }
}

public sealed class KeyEventPayload
{
    public ushort VirtualKey { get; set; }
    public bool Down { get; set; }
    public bool Extended { get; set; }
}

/// <summary>跨屏中文组字：拼音行 + 候选列表（手机置顶展示）。</summary>
public sealed class CompositionUpdatePayload
{
    /// <summary>正在组字的拼音/中间串，如 shi'fou'ke'yi。</summary>
    public string Reading { get; set; } = "";
    /// <summary>当前页候选（已是汉字词组）。</summary>
    public List<string> Candidates { get; set; } = [];
    /// <summary>高亮项 0-based；-1 表示无。</summary>
    public int Highlight { get; set; }
    /// <summary>候选起始编号（通常为 1）。</summary>
    public int NumberBase { get; set; } = 1;
    public bool CanPagePrev { get; set; }
    public bool CanPageNext { get; set; }
}

public sealed class TextInputPayload
{
    public string Text { get; set; } = "";
}

public sealed class CompositionSelectPayload
{
    /// <summary>点选的候选下标（相对当前 Candidates）；优先于 Text。</summary>
    public int Index { get; set; } = -1;
    /// <summary>直接提交的汉字（手机点选时带上，避免 Imm 选词失败）。</summary>
    public string? Text { get; set; }
}

/// <summary>对端是否需要电脑侧组字再下发候选（手机/平板）。</summary>
public static class PeerComposition
{
    public static bool NeedsHostComposition(PeerRemoteInfo? info)
    {
        if (info == null) return false;
        if (string.Equals(info.Platform, PeerPlatform.Android, StringComparison.OrdinalIgnoreCase)
            || string.Equals(info.Platform, PeerPlatform.HarmonyOS, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(info.DeviceClass, PeerDeviceClass.Phone, StringComparison.OrdinalIgnoreCase)
            || string.Equals(info.DeviceClass, PeerDeviceClass.Tablet, StringComparison.OrdinalIgnoreCase))
            return true;
        // Caps 兜底：有绝对指针、无桌面捕获 ≈ 手机/平板（防握手漏 Platform）
        var caps = info.Caps;
        if (caps.Length == 0) return false;
        var abs = false;
        var capture = false;
        foreach (var c in caps)
        {
            if (string.Equals(c, PeerCaps.AbsPointer, StringComparison.OrdinalIgnoreCase)) abs = true;
            if (string.Equals(c, PeerCaps.CaptureInput, StringComparison.OrdinalIgnoreCase)) capture = true;
        }
        return abs && !capture;
    }
}

public sealed class ClipboardTextPayload
{
    public string Text { get; set; } = "";
    public long Sequence { get; set; }
}

public sealed class ClipboardFileAnnounceItem
{
    public string FileName { get; set; } = "";
    public long TotalBytes { get; set; }
}

public sealed class ClipboardFilesAnnouncePayload
{
    public long Sequence { get; set; }
    public string SourceMachine { get; set; } = "";
    public List<ClipboardFileAnnounceItem> Files { get; set; } = [];
}

public sealed class ClipboardFilesPullPayload
{
    public long Sequence { get; set; }
    public string RequesterMachine { get; set; } = "";
}

public sealed class ClipboardImageAnnouncePayload
{
    public long Sequence { get; set; }
    public string SourceMachine { get; set; } = "";
    public long TotalBytes { get; set; }
    public string Format { get; set; } = "png";
}

public sealed class ClipboardImagePullPayload
{
    public long Sequence { get; set; }
    public string RequesterMachine { get; set; } = "";
}

public sealed class ClipboardImageCompletePayload
{
    public long Sequence { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class ClipboardInlineAnnouncePayload
{
    public long Sequence { get; set; }
    public string SourceMachine { get; set; } = "";
    public long TotalBytes { get; set; }
    public bool HasText { get; set; }
    public bool HasHtml { get; set; }
    public bool HasRtf { get; set; }
    public bool HasCsv { get; set; }
    public bool HasImage { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class ClipboardInlinePullPayload
{
    public long Sequence { get; set; }
    public string RequesterMachine { get; set; } = "";
}

public sealed class ClipboardInlineCompletePayload
{
    public long Sequence { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class FileOfferPayload
{
    public Guid TransferId { get; set; }
    public string FileName { get; set; } = "";
    public long TotalBytes { get; set; }
    public long Sequence { get; set; }
    public int FileIndex { get; set; }
    public int FileCount { get; set; }
    /// <summary>直接传输：对端绝对目录；空则写入收件箱（剪贴板粘贴模式）。</summary>
    public string? TargetDirectory { get; set; }
    /// <summary>相对路径（含文件名），用于保留文件夹结构。</summary>
    public string? RelativePath { get; set; }
    /// <summary>是否直接落到指定目录（不进粘贴批处理）。</summary>
    public bool DirectToPath { get; set; }
}

public sealed class RemoteFoldersPayload
{
    public string Desktop { get; set; } = "";
    public string Documents { get; set; } = "";
    public string Downloads { get; set; } = "";
    public string Inbox { get; set; } = "";
    public string MachineName { get; set; } = "";
}

public sealed class FileChunkPayload
{
    public Guid TransferId { get; set; }
    public long Offset { get; set; }
    public byte[] Data { get; set; } = [];
}

public sealed class FileControlPayload
{
    public Guid TransferId { get; set; }
    public string? Reason { get; set; }
}

public sealed class SwitchFocusPayload
{
    public string TargetMachine { get; set; } = "";
    public double EntryX { get; set; }
    public double EntryY { get; set; }
}

public sealed class LayoutSyncPayload
{
    public string SourceMachine { get; set; } = "";
    public List<string> MachineSlots { get; set; } = ["", "", "", ""];
    public bool TwoRow { get; set; }
    /// <summary>UTC 毫秒时间戳，用于忽略过期布局。</summary>
    public long Revision { get; set; }
    /// <summary>
    /// 0=<see cref="LayoutSyncModes.Replace"/> 权威替换；
    /// 1=<see cref="LayoutSyncModes.Merge"/> 并集；
    /// 2=<see cref="LayoutSyncModes.MergeReject"/> 合并失败。
    /// </summary>
    public int Mode { get; set; }
}

/// <summary>高级页「功能 / 行为」跨机同步（不含开机自启、热键、主题语言等本机项）。</summary>
public sealed class BehaviorSyncPayload
{
    public string SourceMachine { get; set; } = "";
    /// <summary>UTC 毫秒时间戳，用于忽略过期设置。</summary>
    public long Revision { get; set; }

    public bool WrapMouse { get; set; }
    public bool ShareClipboard { get; set; } = true;
    public bool TransferFile { get; set; } = true;
    public bool HideMouseAtEdge { get; set; } = true;
    public bool DrawMouseCursor { get; set; } = true;
    public bool SameSubnetOnly { get; set; }
    public bool ShowStatusMessages { get; set; } = true;
    public bool BlockScreenSaver { get; set; } = true;
    public bool MoveMouseRelatively { get; set; }
    public bool BlockMouseAtCorners { get; set; }
    public bool EasyMouse { get; set; } = true;
    /// <summary>0 无障碍 / 1 双击 / 2 极速。</summary>
    public int EdgeCrossMode { get; set; }
}

public sealed class CursorOwnerPayload
{
    /// <summary>当前操作/应显示光标的机器名。</summary>
    public string OwnerMachine { get; set; } = "";
}

public static class Utf8
{
    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    public static string String(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);
}
