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
    SwitchFocus = 50,
    PeerInfo = 60
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

public sealed class FileOfferPayload
{
    public Guid TransferId { get; set; }
    public string FileName { get; set; } = "";
    public long TotalBytes { get; set; }
    public long Sequence { get; set; }
    public int FileIndex { get; set; }
    public int FileCount { get; set; }
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

public static class Utf8
{
    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    public static string String(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);
}
