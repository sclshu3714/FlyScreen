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
    /// <summary>最近一次功能/行为修订（UTC 毫秒），用于跨机同步去重。</summary>
    public long BehaviorRevision { get; set; }
    public bool WrapMouse { get; set; }
    public bool ShareClipboard { get; set; } = true;
    public bool TransferFile { get; set; } = true;
    /// <summary>
    /// 隐藏光标：启用时同一时刻仅活动电脑显示光标，其余隐藏；
    /// 关闭时非活动电脑光标居中可见（仍钉中采集位移）。属性名历史兼容。
    /// </summary>
    public bool HideMouseAtEdge { get; set; } = true;
    public bool DrawMouseCursor { get; set; } = true;
    public bool SameSubnetOnly { get; set; }
    public bool BlockScreenSaver { get; set; } = true;
    public bool MoveMouseRelatively { get; set; }
    public bool BlockMouseAtCorners { get; set; }
    public bool ShowStatusMessages { get; set; } = true;
    public bool EasyMouse { get; set; } = true;
    /// <summary>
    /// Android：通知注入。启用且有用户子程序运行时，以前台通知保活；
    /// 无子程序运行时不占用通知（本机项，不参与 BehaviorSync）。
    /// </summary>
    public bool NotificationInject { get; set; }
    /// <summary>穿边模式：0 无障碍 / 1 双击 / 2 极速（见 <see cref="FlyScreen.Services.Input.EdgeCrossMode"/>）。</summary>
    public int EdgeCrossMode { get; set; }
    /// <summary>最近一次投影目标机器名（弹窗默认项；须双向已连接的电脑/大屏）。</summary>
    public string ScreenExtendPeer { get; set; } = "";
    /// <summary>最近一次投影方式：0 仅电脑 / 1 复制 / 2 扩展 / 3 仅第二屏。</summary>
    public int ScreenExtendProjectionMode { get; set; } = 2;
    /// <summary>投影分辨率来源：0 本机主屏 / 1 目标电脑 / 2 自定义。</summary>
    public int ScreenExtendDisplaySource { get; set; }
    public int ScreenExtendWidth { get; set; }
    public int ScreenExtendHeight { get; set; }
    /// <summary>投影缩放百分比；0 表示跟随来源自动。</summary>
    public int ScreenExtendScalePercent { get; set; }
    /// <summary>
    /// 镜像：MTT/VDD 是否由本程序环境修复安装（权威标记见 Windows 侧 mtt-vdd-installed-by-app.flag）。
    /// </summary>
    public bool MttVddInstalledByApp { get; set; }
    /// <summary>开机自动启动并连接（写入当前用户启动项）。</summary>
    public bool AutoStart { get; set; }
    /// <summary>启动时静默检查 GitHub Release 更新。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;
    /// <summary>界面主题：Light（淡色，默认）/ Dark（深色，待实现）。</summary>
    public string UiTheme { get; set; } = "Light";
    /// <summary>界面语言文化代码，默认 zh-CN。</summary>
    public string UiLanguage { get; set; } = "zh-CN";
    public int TcpPort { get; set; } = 15155;
    public int UdpPort { get; set; } = 15156;
    /// <summary>绑定的本地 IPv4；空表示自动选择。</summary>
    public string PreferredLocalIp { get; set; } = "";
    public string PreferredNicId { get; set; } = "";
    /// <summary>对本机发现/握手名的覆盖；空则自动（WSL 会加发行版后缀避免与宿主机撞名）。</summary>
    public string MachineNameOverride { get; set; } = "";
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

    public static string SettingsPath => FlyScreen.Services.FlyScreenDataPaths.SettingsFile;

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
        // 应用覆盖名（须在首次解析本机名前）
        FlyScreen.Services.Network.LocalPeerIdentity.SetMachineNameOverride(
            string.IsNullOrWhiteSpace(MachineNameOverride) ? null : MachineNameOverride);

        var local = FlyScreen.Services.Network.LocalPeerIdentity.MachineName;
        while (MachineSlots.Count < 4)
            MachineSlots.Add("");

        // 迁移历史错误本机名（Android 曾把 localhost 写入槽位）
        for (var i = 0; i < MachineSlots.Count; i++)
        {
            var m = MachineSlots[i]?.Trim() ?? "";
            if (FlyScreen.Services.Network.LocalPeerIdentity.IsBogusMachineName(m)
                && !string.Equals(m, local, StringComparison.OrdinalIgnoreCase))
            {
                MachineSlots[i] = local;
            }
        }

        // 只保证本机名在槽位中。绝不能按 Environment.MachineName 清空/改写其它槽：
        // 本机显示名与系统名不同时（/etc/hostname、发行版后缀等），同名槽往往是
        // 局域网里另一台真实机器（例如 WSL 测试时的 Windows 宿主机）。
        if (MachineSlots.Any(m => string.Equals(m, local, StringComparison.OrdinalIgnoreCase)))
            return;

        var empty = MachineSlots.FindIndex(string.IsNullOrWhiteSpace);
        if (empty >= 0)
            MachineSlots[empty] = local;
        else
            MachineSlots[0] = local;
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
