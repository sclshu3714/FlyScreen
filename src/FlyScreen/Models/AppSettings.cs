using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyScreen.Models;

public sealed class AppSettings
{
    public string SecurityKey { get; set; } = GenerateKey();
    public List<string> MachineSlots { get; set; } = ["", "", "", ""];
    public bool TwoRow { get; set; }
    /// <summary>最近一次布局修订（UTC 毫秒），用于跨机同步去重。</summary>
    public long LayoutRevision { get; set; }
    public bool WrapMouse { get; set; }
    public bool ShareClipboard { get; set; } = true;
    public bool TransferFile { get; set; } = true;
    public bool HideMouseAtEdge { get; set; } = true;
    public bool DrawMouseCursor { get; set; } = true;
    public bool SameSubnetOnly { get; set; }
    public bool BlockScreenSaver { get; set; } = true;
    public bool MoveMouseRelatively { get; set; }
    public bool BlockMouseAtCorners { get; set; }
    public bool ShowStatusMessages { get; set; } = true;
    public bool EasyMouse { get; set; } = true;
    /// <summary>开机自动启动并连接（写入当前用户启动项）。</summary>
    public bool AutoStart { get; set; }
    /// <summary>启动时静默检查 GitHub Release 更新。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public int TcpPort { get; set; } = 15155;
    public int UdpPort { get; set; } = 15156;
    /// <summary>绑定的本地 IPv4；空表示自动选择。</summary>
    public string PreferredLocalIp { get; set; } = "";
    public string PreferredNicId { get; set; } = "";
    public int SwitchHotkeyMode { get; set; } // 0=F1-F4, 1=1-4, 2=禁用
    public string HotkeySettings { get; set; } = "M";
    public string HotkeyLock { get; set; } = "L";
    public string HotkeyReconnect { get; set; } = "R";
    public string HotkeyExit { get; set; } = "Q";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyScreen", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // ignore corrupt settings
        }

        var s = new AppSettings();
        s.EnsureLocalMachineSlot();
        return s;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void EnsureLocalMachineSlot()
    {
        var local = Environment.MachineName;
        while (MachineSlots.Count < 4)
            MachineSlots.Add("");

        if (!MachineSlots.Any(m => string.Equals(m, local, StringComparison.OrdinalIgnoreCase)))
        {
            var empty = MachineSlots.FindIndex(string.IsNullOrWhiteSpace);
            if (empty >= 0)
                MachineSlots[empty] = local;
            else
                MachineSlots[0] = local;
        }
    }

    public static string GenerateKey()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rng = Random.Shared;
        return string.Create(16, chars, (span, c) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = c[rng.Next(c.Length)];
        });
    }
}
