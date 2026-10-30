using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.Services;

/// <summary>
/// 一键检查 / 一键修复：围绕双向连接常见问题（网卡、发现、防火墙、重连）。
/// </summary>
public sealed class ConnectionDiagnostics
{
    private readonly AppHost _host;

    public ConnectionDiagnostics(AppHost host) => _host = host;

    public async Task<DiagnosticReport> CheckAsync()
    {
        var report = new DiagnosticReport();
        var s = _host.Settings;
        var cm = _host.Connections;

        report.Add("本机", Environment.MachineName, DiagnosticLevel.Info);
        report.Add("监听", cm.IsListening
            ? $"{cm.BindIp}:{s.TcpPort}"
            : "未监听", cm.IsListening ? DiagnosticLevel.Ok : DiagnosticLevel.Error);

        if (string.IsNullOrWhiteSpace(s.SecurityKey) || s.SecurityKey.Length < 4)
            report.Add("安全密钥", "过短或为空，无法与对端握手", DiagnosticLevel.Error);
        else
            report.Add("安全密钥", "已设置", DiagnosticLevel.Ok);

        var nic = NicService.ResolvePreferred(s.PreferredNicId, s.PreferredLocalIp);
        if (nic == null)
            report.Add("网卡", "未解析到可用网卡，请在网络设置中选择", DiagnosticLevel.Error);
        else
            report.Add("网卡", $"{nic.Name} ({nic.IPv4})", DiagnosticLevel.Ok);

        if (!NetworkInterface.GetIsNetworkAvailable())
            report.Add("网络", "系统报告当前无可用网络", DiagnosticLevel.Error);

        var peers = cm.ConfiguredPeers().ToList();
        if (peers.Count == 0)
        {
            report.Add("电脑布局", "未添加对端电脑", DiagnosticLevel.Error);
        }
        else
        {
            report.Add("电脑布局", $"已配置 {peers.Count} 台对端", DiagnosticLevel.Ok);
            foreach (var st in cm.GetConfiguredPeerStatuses())
            {
                var ip = string.IsNullOrEmpty(st.DiscoveredIp) ? "未发现 IP" : st.DiscoveredIp;
                if (st.Bidirectional)
                    report.Add($"对端 {st.Name}", $"双向已连接，发现 IP {ip}", DiagnosticLevel.Ok);
                else if (st.AnyAlive)
                    report.Add($"对端 {st.Name}",
                        $"仅{(st.Outbound ? "出站" : "入站")}连通，发现 IP {ip}（需补齐另一方向）",
                        DiagnosticLevel.Warn);
                else if (string.IsNullOrEmpty(st.DiscoveredIp))
                    report.Add($"对端 {st.Name}", "未连接且局域网未发现（检查网卡/防火墙/密钥/对方是否启动）",
                        DiagnosticLevel.Error);
                else
                    report.Add($"对端 {st.Name}", $"已发现 {ip} 但尚未连通（可点一键修复重连）",
                        DiagnosticLevel.Warn);
            }
        }

        var discovered = cm.Discovery.GetPeers();
        report.Add("局域网发现", discovered.Count == 0
            ? "当前未发现其它玄鸟实例"
            : $"发现 {discovered.Count} 台：" + string.Join("、", discovered.Select(p => p.MachineName)),
            discovered.Count > 0 ? DiagnosticLevel.Ok : DiagnosticLevel.Warn);

        var fw = await CheckFirewallAsync(s.TcpPort, s.UdpPort).ConfigureAwait(false);
        report.Add("防火墙", fw.Message, fw.Ok ? DiagnosticLevel.Ok : DiagnosticLevel.Warn);

        if (s.SameSubnetOnly)
            report.Add("仅同网段", "已启用；跨网段电脑将被拒绝连接", DiagnosticLevel.Warn);

        report.Summary = report.Items.Any(i => i.Level == DiagnosticLevel.Error)
            ? "检查完成：发现必须处理的问题"
            : report.Items.Any(i => i.Level == DiagnosticLevel.Warn)
                ? "检查完成：存在可优化项，可尝试一键修复"
                : "检查完成：当前状态正常";

        return report;
    }

    public async Task<DiagnosticReport> FixAsync(IProgress<string>? progress = null)
    {
        var report = new DiagnosticReport();
        var s = _host.Settings;
        var cm = _host.Connections;

        void Log(string step, string detail, DiagnosticLevel level)
        {
            progress?.Report(step + "…");
            report.Add(step, detail, level);
        }

        try
        {
            // 1) 防火墙放行
            progress?.Report("配置防火墙…");
            var fw = await EnsureFirewallRulesAsync(s.TcpPort, s.UdpPort).ConfigureAwait(false);
            Log("防火墙", fw.Message, fw.Ok ? DiagnosticLevel.Ok : DiagnosticLevel.Warn);

            // 2) 若布局无对端，尝试从发现列表自动填入空槽
            progress?.Report("补全电脑布局…");
            var added = TryFillSlotsFromDiscovery();
            Log("电脑布局", added > 0 ? $"已从发现列表添加 {added} 台" : "无需变更",
                added > 0 ? DiagnosticLevel.Ok : DiagnosticLevel.Info);

            // 3) 重启网络栈（监听 + UDP 发现）
            progress?.Report("重启网络服务…");
            s.Save();
            await _host.RestartNetworkingAsync().ConfigureAwait(false);
            Log("网络服务", $"已在 {cm.BindIp}:{s.TcpPort} 重新监听", DiagnosticLevel.Ok);

            // 4) 等待发现并主动重连
            progress?.Report("等待局域网发现…");
            await Task.Delay(1200).ConfigureAwait(false);
            progress?.Report("刷新连接…");
            await cm.ReconnectNowAsync().ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);
            await cm.ReconnectNowAsync().ConfigureAwait(false);

            var statuses = cm.GetConfiguredPeerStatuses();
            if (statuses.Count == 0)
            {
                Log("连接结果", "仍无配置对端，请先在布局中添加电脑", DiagnosticLevel.Error);
            }
            else
            {
                foreach (var st in statuses)
                {
                    if (st.Bidirectional)
                        Log($"对端 {st.Name}", "双向已连接", DiagnosticLevel.Ok);
                    else if (st.AnyAlive)
                        Log($"对端 {st.Name}",
                            $"仍为单方向（{(st.Outbound ? "出站" : "入站")}）。请确认对方版本一致、密钥相同，并在对方也执行一键修复。",
                            DiagnosticLevel.Warn);
                    else
                        Log($"对端 {st.Name}", "仍未连通。请确认对方已启动玄鸟且网卡/密钥一致。",
                            DiagnosticLevel.Error);
                }
            }

            report.Summary = statuses.Count > 0 && statuses.All(x => x.Bidirectional)
                ? "修复完成：全部对端已双向连接"
                : statuses.Any(x => x.AnyAlive)
                    ? "修复已执行：部分链路仍非双向，请在对端同步操作"
                    : "修复已执行：仍未连通，请对照检查报告排查";
        }
        catch (Exception ex)
        {
            Log("修复异常", ex.Message, DiagnosticLevel.Error);
            report.Summary = "修复过程出错：" + ex.Message;
        }

        return report;
    }

    private int TryFillSlotsFromDiscovery()
    {
        var s = _host.Settings;
        s.EnsureLocalMachineSlot();
        var added = 0;
        foreach (var peer in _host.Connections.Discovery.GetPeers())
        {
            if (string.Equals(peer.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (s.MachineSlots.Any(m => string.Equals(m, peer.MachineName, StringComparison.OrdinalIgnoreCase)))
                continue;
            var idx = s.MachineSlots.FindIndex(string.IsNullOrWhiteSpace);
            if (idx < 0) break;
            s.MachineSlots[idx] = peer.MachineName;
            added++;
        }

        if (added > 0) s.Save();
        return added;
    }

    private static async Task<(bool Ok, string Message)> CheckFirewallAsync(int tcpPort, int udpPort)
    {
        try
        {
            var named = await RunNetshAsync("advfirewall firewall show rule name=\"玄鸟\"").ConfigureAwait(false);
            var tcpRule = await RunNetshAsync("advfirewall firewall show rule name=\"玄鸟 TCP\"").ConfigureAwait(false);
            if ((named.ExitCode == 0 && named.Output.Contains("玄鸟", StringComparison.OrdinalIgnoreCase))
                || (tcpRule.ExitCode == 0 && tcpRule.Output.Contains("玄鸟", StringComparison.OrdinalIgnoreCase)))
                return (true, "已存在「玄鸟」防火墙规则");

            return (false, $"未检测到「玄鸟」专用入站规则（TCP {tcpPort} / UDP {udpPort}），一键修复可添加");
        }
        catch (Exception ex)
        {
            return (false, "无法查询防火墙：" + ex.Message);
        }
    }

    public Task<(bool Ok, string Message)> EnsureListenRulesAsync()
        => EnsureFirewallRulesAsync(_host.Settings.TcpPort, _host.Settings.UdpPort);

    private static async Task<(bool Ok, string Message)> EnsureFirewallRulesAsync(int tcpPort, int udpPort)
    {
        try
        {
            // 先删旧规则再加，避免重复
            await RunNetshAsync("advfirewall firewall delete rule name=\"玄鸟\"").ConfigureAwait(false);
            await RunNetshAsync("advfirewall firewall delete rule name=\"玄鸟 TCP\"").ConfigureAwait(false);
            await RunNetshAsync("advfirewall firewall delete rule name=\"玄鸟 UDP\"").ConfigureAwait(false);

            var exe = Environment.ProcessPath ?? "";
            var r1 = await RunNetshAsync(
                $"advfirewall firewall add rule name=\"玄鸟 TCP\" dir=in action=allow protocol=TCP localport={tcpPort} profile=any").ConfigureAwait(false);
            var r2 = await RunNetshAsync(
                $"advfirewall firewall add rule name=\"玄鸟 UDP\" dir=in action=allow protocol=UDP localport={udpPort} profile=any").ConfigureAwait(false);

            if (!string.IsNullOrEmpty(exe))
            {
                await RunNetshAsync(
                    $"advfirewall firewall add rule name=\"玄鸟\" dir=in action=allow program=\"{exe}\" enable=yes profile=any").ConfigureAwait(false);
            }

            if (r1.ExitCode == 0 && r2.ExitCode == 0)
                return (true, $"已放行 TCP {tcpPort}、UDP {udpPort} 及程序入站");

            return (false, $"添加规则可能失败：TCP={r1.ExitCode} UDP={r2.ExitCode}。请确认以管理员运行。");
        }
        catch (Exception ex)
        {
            return (false, "配置防火墙失败：" + ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunNetshAsync(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return (-1, "无法启动 netsh");
        var stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return (p.ExitCode, stdout + stderr);
    }
}

public enum DiagnosticLevel
{
    Info,
    Ok,
    Warn,
    Error
}

public sealed class DiagnosticItem
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public DiagnosticLevel Level { get; init; }
}

public sealed class DiagnosticReport
{
    public string Summary { get; set; } = "";
    public List<DiagnosticItem> Items { get; } = [];

    public void Add(string title, string detail, DiagnosticLevel level)
        => Items.Add(new DiagnosticItem { Title = title, Detail = detail, Level = level });

    public string FormatText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary);
        sb.AppendLine();
        foreach (var i in Items)
        {
            var tag = i.Level switch
            {
                DiagnosticLevel.Ok => "✓",
                DiagnosticLevel.Warn => "!",
                DiagnosticLevel.Error => "✗",
                _ => "·"
            };
            sb.AppendLine($"{tag} [{i.Title}] {i.Detail}");
        }

        return sb.ToString().TrimEnd();
    }
}
