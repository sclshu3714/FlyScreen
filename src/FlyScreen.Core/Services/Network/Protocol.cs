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
    /// <summary>删除传输列表项：对端同步删记录；接收侧同时删已收文件。</summary>
    FileTransferRemove = 59,
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
    /// <summary>安全密钥同步（布局内已连接 + 密钥不同未连上的发现实例）。</summary>
    SecurityKeySync = 65,
    /// <summary>查询对端主屏分辨率/缩放。</summary>
    DisplayInfoQuery = 66,
    /// <summary>对端主屏分辨率/缩放应答。</summary>
    DisplayInfoReply = 67,
    /// <summary>电脑→手机：跨屏中文组字状态（拼音 + 候选）。</summary>
    CompositionUpdate = 70,
    /// <summary>电脑→手机：取消组字浮层。</summary>
    CompositionCancel = 71,
    /// <summary>电脑→手机：已确认文本（插入目标）。</summary>
    TextInput = 72,
    /// <summary>手机→电脑：点选候选（Index 或直接 Text）。</summary>
    CompositionSelect = 73,

    /// <summary>电脑扩展屏：主机→从机发起会话。</summary>
    ScreenExtendOffer = 80,
    /// <summary>电脑扩展屏：从机接受。</summary>
    ScreenExtendAccept = 81,
    /// <summary>电脑扩展屏：从机拒绝。</summary>
    ScreenExtendReject = 82,
    /// <summary>电脑扩展屏：任一方结束会话。</summary>
    ScreenExtendStop = 83,
    /// <summary>电脑扩展屏：JPEG 帧（Bulk；见 ScreenExtendFrameCodec）。</summary>
    ScreenExtendFrame = 84,

    /// <summary>穿边拖文件：光标所在机器请回报放下目录。</summary>
    EdgeDragQuery = 85,
    /// <summary>穿边拖文件：放下目录（接收端绝对路径）。</summary>
    EdgeDragPlace = 86
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

    /// <summary>
    /// 连接通道：0=<see cref="PeerChannel.Control"/> 键鼠/信令；
    /// 1=<see cref="PeerChannel.Bulk"/> 文件与剪贴板分块。旧端忽略则按控制面处理。
    /// </summary>
    public int Channel { get; set; }

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
    /// <summary>支持独立大块传输连接（文件/剪贴板分块），与键鼠控制面分离。</summary>
    public const string BulkTransfer = "BulkTransfer";
    /// <summary>可作为扩展屏主机（占用第二输出并推流）。</summary>
    public const string ScreenExtendHost = "ScreenExtendHost";
    /// <summary>可作为扩展屏从机（全屏收流）。</summary>
    public const string ScreenExtendSink = "ScreenExtendSink";

    public static readonly string[] DesktopFull =
    [
        EdgeMouse, AbsPointer, ClipboardText, ClipboardFiles,
        DirectTransfer, LayoutSync, InjectInput, CaptureInput, BulkTransfer,
        ScreenExtendHost, ScreenExtendSink
    ];

    /// <summary>macOS 桌面：与 Win 对齐（键鼠 + 剪贴板文本/文件 + 直传 + 布局）。</summary>
    public static readonly string[] MacDesktopW1 =
    [
        EdgeMouse, AbsPointer, ClipboardText, ClipboardFiles,
        DirectTransfer, LayoutSync, InjectInput, CaptureInput, BulkTransfer,
        ScreenExtendHost, ScreenExtendSink
    ];

    /// <summary>Linux X11 桌面：与 Win 对齐。</summary>
    public static readonly string[] LinuxDesktopX11 =
    [
        EdgeMouse, AbsPointer, ClipboardText, ClipboardFiles,
        DirectTransfer, LayoutSync, InjectInput, CaptureInput, BulkTransfer,
        ScreenExtendHost, ScreenExtendSink
    ];

    /// <summary>Linux Wayland：无 EdgeMouse/捕获；剪贴板文本/文件 + 直传 + 有限注入。</summary>
    public static readonly string[] LinuxDesktopWayland =
    [
        AbsPointer, ClipboardText, ClipboardFiles, DirectTransfer, LayoutSync, InjectInput, BulkTransfer,
        ScreenExtendHost, ScreenExtendSink
    ];

    /// <summary>无图形会话：直传落盘 + 文本通道（无系统粘贴体验）；ClipboardFiles 表示可收文件到 Inbox。</summary>
    public static readonly string[] LinuxHeadless =
    [
        ClipboardText, ClipboardFiles, DirectTransfer, BulkTransfer
    ];

    /// <summary>
    /// Android 手机/平板：触控板控桌面 + 文本剪贴板 + 收文件；
    /// 另宣告 EdgeMouse/InjectInput，便于桌面边缘切入并由无障碍服务注入键鼠（须用户授权）。
    /// ScreenExtendHost：可「复制」投屏到电脑（MediaProjection）；不含 Sink（不作接收端）。
    /// 不含 LayoutSync（不参与桌面布局同步主路径）。
    /// 功能/行为同步（BehaviorSync）不依赖 LayoutSync，手机仍会收到穿边模式等设置。
    /// </summary>
    public static readonly string[] AndroidMobile =
    [
        EdgeMouse, AbsPointer, ClipboardText, DirectTransfer, InjectInput, BulkTransfer,
        ScreenExtendHost
    ];

    /// <summary>鸿蒙手机/平板（W5a）：对标 AndroidMobile。</summary>
    public static readonly string[] HarmonyMobile =
    [
        EdgeMouse, AbsPointer, ClipboardText, DirectTransfer, InjectInput, BulkTransfer,
        ScreenExtendHost
    ];

    /// <summary>鸿蒙电脑（W5b）：对标桌面；API 不足时由宿主降级去掉 EdgeMouse/Capture。</summary>
    public static readonly string[] HarmonyDesktop =
    [
        EdgeMouse, AbsPointer, ClipboardText, ClipboardFiles,
        DirectTransfer, LayoutSync, InjectInput, CaptureInput, BulkTransfer,
        ScreenExtendHost, ScreenExtendSink
    ];
}

/// <summary>握手通道类型：控制面 / 大块传输面。</summary>
public static class PeerChannel
{
    public const int Control = 0;
    public const int Bulk = 1;
}

/// <summary>应走 bulk 连接的消息（大块数据）；宣告/拉取信令仍走控制面。</summary>
public static class BulkMessageTypes
{
    public static bool IsBulk(MessageType type) => type switch
    {
        MessageType.FileOffer or MessageType.FileAccept or MessageType.FileChunk
            or MessageType.FileComplete or MessageType.FileAbort
            or MessageType.ClipboardInlineChunk or MessageType.ClipboardInlineComplete
            or MessageType.ClipboardImageChunk or MessageType.ClipboardImageComplete
            or MessageType.ScreenExtendFrame
            => true,
        _ => false
    };
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

    /// <summary>
    /// 非空时按 Unicode 插入到焦点处，忽略 VirtualKey。
    /// 不经剪贴板，避免盖掉对端剪贴板，也避免字母被对端输入法再组字。
    /// </summary>
    public string? Text { get; set; }
}

/// <summary>跨屏中文组字：拼音行 + 候选列表（手机置顶展示）。</summary>
public sealed class CompositionUpdatePayload
{
    /// <summary>正在组字的拼音/中间串，如 shi'fou'ke'yi。</summary>
    public string Reading { get; set; } = "";
    /// <summary>当前页候选（已是汉字词组）。</summary>
    public List<string> Candidates { get; set; } = [];
    /// <summary>高亮项 0-based（相对当前 Candidates）；-1 表示无。</summary>
    public int Highlight { get; set; }
    /// <summary>候选起始编号（单行通常为 1）。</summary>
    public int NumberBase { get; set; } = 1;
    public bool CanPagePrev { get; set; }
    public bool CanPageNext { get; set; }
    /// <summary>多行候选模式（逻辑上按 GridColumns 分行，数字键 1–N 只作用于当前行）。</summary>
    public bool MultiLine { get; set; }
    /// <summary>每行逻辑列数（默认 5；手机纵向展示时按此分行与编号）。</summary>
    public int GridColumns { get; set; } = 5;
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
    /// <summary>是否为文件夹（旧端忽略未知字段；新端占位/展开传输用）。</summary>
    public bool IsDirectory { get; set; }
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
    /// <summary>传输列表分组键（文件夹名）；空=独立文件一行。</summary>
    public string? GroupKey { get; set; }
    /// <summary>组内序号（0 起）。</summary>
    public int GroupIndex { get; set; }
    /// <summary>组内文件总数；&gt;1 时列表合并为一行并显示 1/N。</summary>
    public int GroupCount { get; set; }
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

/// <summary>任一侧删除传输列表项时通知对端同步清理。</summary>
public sealed class FileTransferRemovePayload
{
    public long Sequence { get; set; }
    /// <summary>文件夹批量键；空表示单文件。</summary>
    public string? GroupKey { get; set; }
    /// <summary>单文件 TransferId（与双方列表项 Id 一致）；文件夹批量为 Empty。</summary>
    public Guid TransferId { get; set; }
    public string? FileName { get; set; }
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

/// <summary>布局内同步安全密钥（TCP 已连接；UDP 给密钥不同尚未握手成功的实例）。</summary>
public sealed class SecurityKeySyncPayload
{
    /// <summary>UDP 包判别；TCP JSON 可省略。</summary>
    public string? Kind { get; set; }
    public string SourceMachine { get; set; } = "";
    public string SecurityKey { get; set; } = "";
    /// <summary>UTC 毫秒时间戳，忽略过期推送。</summary>
    public long Revision { get; set; }
    /// <summary>发送方当前电脑布局槽位（接收方校验是否同布局）。</summary>
    public List<string> LayoutSlots { get; set; } = [];
    /// <summary>UDP 可选：发送方宣告 IP。</summary>
    public string? Ip { get; set; }
    /// <summary>UDP 可选：发送方 TCP 端口。</summary>
    public int TcpPort { get; set; }
}

/// <summary>出站 TCP 失败时，请对端主动连回本机（发现口 UDP）。</summary>
public sealed class DialRequestPayload
{
    public string? Kind { get; set; }
    public string SourceMachine { get; set; } = "";
    public string? Ip { get; set; }
    public int TcpPort { get; set; }
}

public sealed class ScreenExtendOfferPayload
{
    public string SessionId { get; set; } = "";
    public int PreferredWidth { get; set; } = 1920;
    public int PreferredHeight { get; set; } = 1080;
    public int Fps { get; set; } = 12;
}

public sealed class ScreenExtendAcceptPayload
{
    public string SessionId { get; set; } = "";
    public int SinkWidth { get; set; }
    public int SinkHeight { get; set; }
    /// <summary>从机主屏缩放百分比（100=100%）；旧端缺省 0 表示未知。</summary>
    public int SinkScalePercent { get; set; }
}

/// <summary>对端主屏显示信息（投影弹窗默认值 / 扩展副屏对齐）。</summary>
public sealed class DisplayInfoPayload
{
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>100 / 125 / 150 / 175 / 200 …</summary>
    public int ScalePercent { get; set; } = 100;
    public int RefreshHz { get; set; } = 60;
}

public sealed class ScreenExtendRejectPayload
{
    public string SessionId { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class EdgeDragQueryPayload
{
    public string SessionId { get; set; } = "";
}

public sealed class EdgeDragPlacePayload
{
    public string SessionId { get; set; } = "";
    /// <summary>接收端上的绝对目录。空表示放不了。</summary>
    public string Directory { get; set; } = "";
}

public sealed class ScreenExtendStopPayload
{
    public string SessionId { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>
/// ScreenExtendFrame 二进制：16 字节 SessionId（Guid）+ int32 W + int32 H + int32 Seq + JPEG。
/// </summary>
public static class ScreenExtendFrameCodec
{
    public const int HeaderSize = 28;

    public static byte[] Encode(Guid sessionId, int width, int height, int seq, ReadOnlySpan<byte> jpeg)
    {
        var buf = new byte[HeaderSize + jpeg.Length];
        sessionId.TryWriteBytes(buf.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(24, 4), seq);
        jpeg.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload,
        out Guid sessionId, out int width, out int height, out int seq, out ReadOnlySpan<byte> jpeg)
    {
        sessionId = default;
        width = height = seq = 0;
        jpeg = default;
        if (payload.Length < HeaderSize) return false;
        sessionId = new Guid(payload[..16]);
        width = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(20, 4));
        seq = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(24, 4));
        jpeg = payload[HeaderSize..];
        return true;
    }
}

public static class Utf8
{
    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    public static string String(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);
}
