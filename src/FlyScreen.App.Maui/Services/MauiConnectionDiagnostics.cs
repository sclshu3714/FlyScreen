using System.Net.NetworkInformation;
using System.Text;
using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui.Services;

/// <summary>对齐 Win ConnectionDiagnostics：一键检查 / 一键修复（重连 + Windows 防火墙放行）。</summary>
public sealed class MauiConnectionDiagnostics
{
    private readonly MauiAppHost _host;

    public MauiConnectionDiagnostics(MauiAppHost host) => _host = host;

    public async Task<MauiDiagnosticReport> CheckAsync()
    {
        var report = new MauiDiagnosticReport();
        var s = _host.Settings;
        var cm = _host.Connections;

        report.Add("本机", LocalPeerIdentity.MachineName, MauiDiagnosticLevel.Info);
        report.Add("监听", cm.IsListening
            ? $"{cm.BindIp}:{s.TcpPort}"
            : "未监听", cm.IsListening ? MauiDiagnosticLevel.Ok : MauiDiagnosticLevel.Error);

        if (string.IsNullOrWhiteSpace(s.SecurityKey) || s.SecurityKey.Length < 4)
            report.Add("安全密钥", "过短或为空，无法与对端握手", MauiDiagnosticLevel.Error);
        else
            report.Add("安全密钥", "已设置", MauiDiagnosticLevel.Ok);

        var nic = NicService.ResolvePreferred(s.PreferredNicId, s.PreferredLocalIp);
        if (nic == null)
            report.Add("网卡", "未解析到可用网卡，请在网络设置中选择", MauiDiagnosticLevel.Error);
        else
            report.Add("网卡", $"{nic.Name} ({nic.IPv4})", MauiDiagnosticLevel.Ok);

        if (!NetworkInterface.GetIsNetworkAvailable())
            report.Add("网络", "系统报告当前无可用网络", MauiDiagnosticLevel.Error);

        var peers = cm.ConfiguredPeers().ToList();
        if (peers.Count == 0)
        {
            report.Add("电脑布局", "未添加对端电脑", MauiDiagnosticLevel.Error);
        }
        else
        {
            report.Add("电脑布局", $"已配置 {peers.Count} 台对端", MauiDiagnosticLevel.Ok);
            foreach (var st in cm.GetConfiguredPeerStatuses())
            {
                var ip = string.IsNullOrEmpty(st.DiscoveredIp) ? "未发现 IP" : st.DiscoveredIp;
                if (st.Bidirectional)
                    report.Add($"对端 {st.Name}", $"双向已连接，发现 IP {ip}", MauiDiagnosticLevel.Ok);
                else if (st.AnyAlive)
                    report.Add($"对端 {st.Name}",
                        $"仅{(st.Outbound ? "出站" : "入站")}连通，发现 IP {ip}（需补齐另一方向）",
                        MauiDiagnosticLevel.Warn);
                else if (string.IsNullOrEmpty(st.DiscoveredIp))
                    report.Add($"对端 {st.Name}", "未连接且局域网未发现（检查网卡/防火墙/密钥/对方是否启动）",
                        MauiDiagnosticLevel.Error);
                else
                    report.Add($"对端 {st.Name}", $"已发现 {ip} 但尚未连通（可点一键修复重连）",
                        MauiDiagnosticLevel.Warn);
            }
        }

        var discovered = cm.Discovery.GetPeers();
        report.Add("局域网发现", discovered.Count == 0
            ? "当前未发现其它玄鸟实例"
            : $"发现 {discovered.Count} 台：" + string.Join("、", discovered.Select(p => p.MachineName)),
            discovered.Count > 0 ? MauiDiagnosticLevel.Ok : MauiDiagnosticLevel.Warn);

#if WINDOWS
        var fw = await CheckFirewallAsync(s.TcpPort, s.UdpPort).ConfigureAwait(false);
        report.Add("防火墙", fw.Message, fw.Ok ? MauiDiagnosticLevel.Ok : MauiDiagnosticLevel.Warn);
#else
        report.Add("防火墙", "请确认本机防火墙已放行 TCP/UDP 端口", MauiDiagnosticLevel.Info);
        await Task.CompletedTask;
#endif

        if (s.SameSubnetOnly)
            report.Add("仅同网段", "已启用；跨网段电脑将被拒绝连接", MauiDiagnosticLevel.Warn);

        report.Summary = report.Items.Any(i => i.Level == MauiDiagnosticLevel.Error)
            ? "检查完成：发现必须处理的问题"
            : report.Items.Any(i => i.Level == MauiDiagnosticLevel.Warn)
                ? "检查完成：存在可优化项，可尝试一键修复"
                : "检查完成：当前状态正常";
        return report;
    }

    public async Task<MauiDiagnosticReport> FixAsync(IProgress<string>? progress = null)
    {
        var report = new MauiDiagnosticReport();
        var s = _host.Settings;
        var cm = _host.Connections;

        void Log(string step, string detail, MauiDiagnosticLevel level)
        {
            progress?.Report(step + "…");
            report.Add(step, detail, level);
        }

#if WINDOWS
        progress?.Report("防火墙…");
        var fw = await EnsureListenRulesAsync(s.TcpPort, s.UdpPort).ConfigureAwait(false);
        Log("防火墙", fw.Message, fw.Ok ? MauiDiagnosticLevel.Ok : MauiDiagnosticLevel.Warn);
#endif

        progress?.Report("重新连接…");
        try
        {
            await cm.ReconnectNowAsync().ConfigureAwait(false);
            Log("重新连接", "已触发全量重连", MauiDiagnosticLevel.Ok);
        }
        catch (Exception ex)
        {
            Log("重新连接", ex.Message, MauiDiagnosticLevel.Error);
        }

        await Task.Delay(800).ConfigureAwait(false);
        var check = await CheckAsync().ConfigureAwait(false);
        foreach (var item in check.Items)
            report.Items.Add(item);
        report.Summary = "修复完成 · " + check.Summary;
        return report;
    }

#if WINDOWS
    private static async Task<(bool Ok, string Message)> CheckFirewallAsync(int tcp, int udp)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=all",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return (false, "无法查询防火墙规则");
            var output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            var has = output.Contains("FlyScreen", StringComparison.OrdinalIgnoreCase)
                      || output.Contains("玄鸟", StringComparison.OrdinalIgnoreCase);
            return has
                ? (true, "已检测到玄鸟相关防火墙规则")
                : (false, $"未检测到放行规则，建议一键修复（TCP {tcp} / UDP {udp}）");
        }
        catch (Exception ex)
        {
            return (false, "查询防火墙失败：" + ex.Message);
        }
    }

    public static async Task<(bool Ok, string Message)> EnsureListenRulesAsync(int tcp, int udp)
    {
        try
        {
            async Task<int> Run(string args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return -1;
                await p.WaitForExitAsync().ConfigureAwait(false);
                return p.ExitCode;
            }

            const string name = "FlyScreen-XuanNiao";
            await Run($"advfirewall firewall delete rule name=\"{name}\"").ConfigureAwait(false);
            var tcpIn = await Run($"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={tcp} profile=any").ConfigureAwait(false);
            var udpIn = await Run($"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=UDP localport={udp} profile=any").ConfigureAwait(false);
            await Run($"advfirewall firewall add rule name=\"{name}\" dir=out action=allow protocol=TCP localport={tcp} profile=any").ConfigureAwait(false);
            await Run($"advfirewall firewall add rule name=\"{name}\" dir=out action=allow protocol=UDP localport={udp} profile=any").ConfigureAwait(false);
            if (tcpIn == 0 && udpIn == 0)
                return (true, $"已放行防火墙 TCP {tcp} / UDP {udp}");
            return (false, "写入防火墙失败（需要管理员权限），对端将无法连入本机");
        }
        catch (Exception ex)
        {
            return (false, "写入防火墙失败（可能需要管理员权限）：" + ex.Message);
        }
    }
#endif
}

public enum MauiDiagnosticLevel { Ok, Warn, Error, Info }

public sealed class MauiDiagnosticReport
{
    public string Summary { get; set; } = "";
    public List<MauiDiagnosticItem> Items { get; } = [];

    public void Add(string title, string detail, MauiDiagnosticLevel level)
        => Items.Add(new MauiDiagnosticItem(title, detail, level));

    public string FormatText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary);
        sb.AppendLine();
        foreach (var i in Items)
            sb.AppendLine($"[{LevelTag(i.Level)}] {i.Title}: {i.Detail}");
        return sb.ToString();
    }

    private static string LevelTag(MauiDiagnosticLevel l) => l switch
    {
        MauiDiagnosticLevel.Ok => "OK",
        MauiDiagnosticLevel.Warn => "WARN",
        MauiDiagnosticLevel.Error => "ERR",
        _ => "INFO"
    };
}

public sealed record MauiDiagnosticItem(string Title, string Detail, MauiDiagnosticLevel Level);
