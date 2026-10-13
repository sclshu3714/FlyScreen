using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 内联剪贴板内容（非文件）：文本 / HTML / RTF / CSV / PNG。
/// 打包为单一二进制块，便于宣告后按需分块拉取。
/// </summary>
public sealed class ClipboardInlineData
{
    public string? Text { get; init; }
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    public string? Csv { get; init; }
    public byte[]? Png { get; init; }

    public bool IsEmpty =>
        string.IsNullOrEmpty(Text)
        && string.IsNullOrEmpty(Html)
        && string.IsNullOrEmpty(Rtf)
        && string.IsNullOrEmpty(Csv)
        && Png is not { Length: > 0 };

    public string BuildSummary()
    {
        var parts = new List<string>(5);
        if (!string.IsNullOrEmpty(Text)) parts.Add("文本");
        if (!string.IsNullOrEmpty(Html)) parts.Add("HTML");
        if (!string.IsNullOrEmpty(Rtf)) parts.Add("RTF");
        if (!string.IsNullOrEmpty(Csv)) parts.Add("CSV");
        if (Png is { Length: > 0 }) parts.Add("图片");
        return parts.Count == 0 ? "空" : string.Join("+", parts);
    }

    public string BuildKey()
    {
        static string Sample(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "0";
            var len = s.Length;
            var head = len > 24 ? s[..24] : s;
            var tail = len > 48 ? s[^24..] : "";
            return $"{len}:{head}:{tail}";
        }

        var img = Png is { Length: > 0 } ? ClipboardBridge.BuildImageKey(Png) : "0";
        return $"t={Sample(Text)}|h={Sample(Html)}|r={Sample(Rtf)}|c={Sample(Csv)}|i={img}";
    }

    public byte[] Pack()
    {
        using var ms = new MemoryStream();
        WriteString(ms, Text);
        WriteString(ms, Html);
        WriteString(ms, Rtf);
        WriteString(ms, Csv);
        WriteBytes(ms, Png);
        return ms.ToArray();
    }

    public static ClipboardInlineData Unpack(byte[] blob)
    {
        using var ms = new MemoryStream(blob, writable: false);
        return new ClipboardInlineData
        {
            Text = ReadString(ms),
            Html = ReadString(ms),
            Rtf = ReadString(ms),
            Csv = ReadString(ms),
            Png = ReadBytes(ms)
        };
    }

    /// <summary>兼容旧版：整包视为 PNG。</summary>
    public static ClipboardInlineData FromPngOnly(byte[] png)
        => new() { Png = png };

    private static void WriteString(Stream s, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteInt32(s, 0);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(s, bytes.Length);
        s.Write(bytes);
    }

    private static void WriteBytes(Stream s, byte[]? value)
    {
        if (value is not { Length: > 0 })
        {
            WriteInt32(s, 0);
            return;
        }

        WriteInt32(s, value.Length);
        s.Write(value);
    }

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        s.Write(buf);
    }

    private static string? ReadString(Stream s)
    {
        var bytes = ReadBytes(s);
        return bytes is { Length: > 0 } ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static byte[]? ReadBytes(Stream s)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        if (s.Read(lenBuf) != 4) return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (len <= 0) return null;
        if (len > 64 * 1024 * 1024) throw new InvalidDataException("内联剪贴板块过大");
        var data = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = s.Read(data, read, len - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return data;
    }
}
