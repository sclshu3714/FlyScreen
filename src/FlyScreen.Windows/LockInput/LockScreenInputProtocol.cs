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
    }

    public static string Encode(Command cmd)
        => JsonSerializer.Serialize(cmd, JsonOptions);

    public static Command? Decode(string line)
    {
        try { return JsonSerializer.Deserialize<Command>(line, JsonOptions); }
        catch { return null; }
    }
}
