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
    LayoutSync = 61
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
    public int ProtocolVersion { get; set; } = 1;
    public bool IsReply { get; set; }
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
}

public static class Utf8
{
    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    public static string String(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);
}
