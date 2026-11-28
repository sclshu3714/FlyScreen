using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyScreen.LockInput;

/// <summary>用户态 ↔ 锁屏助手命名管道协议（每行一条 JSON）。</summary>
public static class LockScreenInputProtocol
{
    public const string PipeName = "FlyScreen.LockInputHelper";
    public const string ServiceName = "FlyScreenLockInputHelper";
    public const string ExeName = "FlyScreen.LockInputHelper.exe";
    public const string BinName = "FlyScreen.LockInputHelper.bin";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public sealed class Command
    {
        public string Op { get; set; } = "";
        public double? Nx { get; set; }
        public double? Ny { get; set; }
        public int? Dx { get; set; }
        public int? Dy { get; set; }
        public int? Button { get; set; }
        public bool? Down { get; set; }
        public int? Delta { get; set; }
        public bool? Horizontal { get; set; }
        public ushort? Vk { get; set; }
        public bool? Extended { get; set; }
        public string? Text { get; set; }

        /// <summary>抓屏区域（物理像素）；宽高 ≤0 表示主屏全屏。</summary>
        public int? Left { get; set; }
        public int? Top { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public long? Quality { get; set; }
        public int? MaxEdge { get; set; }
        public bool? DrawCursor { get; set; }
    }

    /// <summary>需应答的操作（desktop / capture）返回一行 JSON。</summary>
    public sealed class Response
    {
        public string Op { get; set; } = "";
        public bool Ok { get; set; }
        public string? Desktop { get; set; }
        public bool? Secure { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public string? JpegBase64 { get; set; }
        public string? Error { get; set; }
    }

    public static string Encode(Command cmd)
        => JsonSerializer.Serialize(cmd, JsonOptions);

    public static string Encode(Response resp)
        => JsonSerializer.Serialize(resp, JsonOptions);

    public static Command? Decode(string line)
    {
        try { return JsonSerializer.Deserialize<Command>(line, JsonOptions); }
        catch { return null; }
    }

    public static Response? DecodeResponse(string line)
    {
        try { return JsonSerializer.Deserialize<Response>(line, JsonOptions); }
        catch { return null; }
    }
}
