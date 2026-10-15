using System.Net.NetworkInformation;
using System.Text;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.DesktopUi.Services;

/// <summary>
/// 桌面端（Mac/Linux）连接诊断：与 Win ConnectionDiagnostics 报告结构对齐；
/// 防火墙放行等 Windows 专属步骤在此跳过或改为平台提示。
/// </summary>
public sealed class DesktopConnectionDiagnostics
{
    private readonly IDesktopShellHost _host;

    public DesktopConnectionDiagnostics(IDesktopShellHost host) => _host = host;

    public async Task<DesktopDiagnosticReport> CheckAsync()
    {
        var report = new DesktopDiagnosticReport();
        var s = _host.Settings;
        var cm = _host.Connections;

        report.Add("本机", Environment.MachineName, DesktopDiagnosticLevel.Info);
        report.Add("监听", cm.IsListening
            ? $"{cm.BindIp}:{s.TcpPort}"
            : "未监听", cm.IsListening ? DesktopDiagnosticLevel.Ok : DesktopDiagnosticLevel.Error);

        if (string.IsNullOrWhiteSpace(s.SecurityKey) || s.SecurityKey.Length < 4)
            report.Add("安全密钥", "过短或为空，无法与对端握手", DesktopDiagnosticLevel.Error);
        else
            report.Add("安全密钥", "已设置", DesktopDiagnosticLevel.Ok);

        var nic = NicService.ResolvePreferred(s.PreferredNicId, s.PreferredLocalIp);
        if (nic == null)
            report.Add("网卡", "未解析到可用网卡，请在网络设置中选择", DesktopDiagnosticLevel.Error);
        else
            report.Add("网卡", $"{nic.Name} ({nic.IPv4})", DesktopDiagnosticLevel.Ok);

        if (!NetworkInterface.GetIsNetworkAvailable())
            report.Add("网络", "系统报告当前无可用网络", DesktopDiagnosticLevel.Error);

        var peers = cm.ConfiguredPeers().ToList();
        if (peers.Count == 0)
        {
            report.Add("电脑布局", "未添加对端电脑", DesktopDiagnosticLevel.Error);
        }
        else
        {
            report.Add("电脑布局", $"已配置 {peers.Count} 台对端", DesktopDiagnosticLevel.Ok);
            foreach (var st in cm.GetConfiguredPeerStatuses())
            {
                var ip = string.IsNullOrEmpty(st.DiscoveredIp) ? "未发现 IP" : st.DiscoveredIp;
                if (st.Bidirectional)
                    report.Add($"对端 {st.Name}", $"双向已连接，发现 IP {ip}", DesktopDiagnosticLevel.Ok);
                else if (st.AnyAlive)
                    report.Add($"对端 {st.Name}",
                        $"仅{(st.Outbound ? "出站" : "入站")}连通，发现 IP {ip}（需补齐另一方向）",
                        DesktopDiagnosticLevel.Warn);
                else if (string.IsNullOrEmpty(st.DiscoveredIp))
                    report.Add($"对端 {st.Name}", "未连接且局域网未发现（检查网卡/防火墙/密钥/对方是否启动）",
                        DesktopDiagnosticLevel.Error);
                else
                    report.Add($"对端 {st.Name}", $"已发现 {ip} 但尚未连通（可点一键修复重连）",
                        DesktopDiagnosticLevel.Warn);
            }
        }

        var discovered = cm.Discovery.GetPeers();
        report.Add("局域网发现", discovered.Count == 0
            ? "当前未发现其它玄鸟实例"
            : $"发现 {discovered.Count} 台：" + string.Join("、", discovered.Select(p => p.MachineName)),
            discovered.Count > 0 ? DesktopDiagnosticLevel.Ok : DesktopDiagnosticLevel.Warn);

        report.Add("防火墙",
            OperatingSystem.IsWindows()
                ? "请在 Windows 端使用帮助窗口一键修复放行端口"
                : "请确认本机防火墙已放行 TCP/UDP 发现端口（见帮助「网络与防火墙」）",
            DesktopDiagnosticLevel.Info);

        if (s.SameSubnetOnly)
            report.Add("仅同网段", "已启用；跨网段电脑将被拒绝连接", DesktopDiagnosticLevel.Warn);

        report.Add("平台", _host.PlatformStatusLine, DesktopDiagnosticLevel.Info);

        report.Summary = report.Items.Any(i => i.Level == DesktopDiagnosticLevel.Error)
            ? "检查完成：发现必须处理的问题"
            : report.Items.Any(i => i.Level == DesktopDiagnosticLevel.Warn)
                ? "检查完成：存在可优化项，可尝试一键修复"
                : "检查完成：当前状态正常";

        await Task.CompletedTask.ConfigureAwait(false);
        return report;
    }

    public async Task<DesktopDiagnosticReport> FixAsync(IProgress<string>? progress = null)
    {
        var report = new DesktopDiagnosticReport();
        var s = _host.Settings;
        var cm = _host.Connections;

        void Log(string step, string detail, DesktopDiagnosticLevel level)
        {
            progress?.Report(step + "…");
            report.Add(step, detail, level);
        }

        try
        {
            progress?.Report("补全电脑布局…");
            var added = TryFillSlotsFromDiscovery();
            Log("电脑布局", added > 0 ? $"已从发现列表添加 {added} 台" : "无需变更",
                added > 0 ? DesktopDiagnosticLevel.Ok : DesktopDiagnosticLevel.Info);

            progress?.Report("重启网络服务…");
            s.Save();
            await _host.RestartNetworkingAsync().ConfigureAwait(false);
            Log("网络服务", $"已在 {cm.BindIp}:{s.TcpPort} 重新监听", DesktopDiagnosticLevel.Ok);

            progress?.Report("等待局域网发现…");
            await Task.Delay(1200).ConfigureAwait(false);
            progress?.Report("刷新连接…");
            await cm.ReconnectNowAsync().ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);
            await cm.ReconnectNowAsync().ConfigureAwait(false);

            var statuses = cm.GetConfiguredPeerStatuses();
            if (statuses.Count == 0)
            {
                Log("连接结果", "仍无配置对端，请先在布局中添加电脑", DesktopDiagnosticLevel.Error);
            }
            else
            {
                foreach (var st in statuses)
                {
                    if (st.Bidirectional)
                        Log($"对端 {st.Name}", "双向已连接", DesktopDiagnosticLevel.Ok);
                    else if (st.AnyAlive)
                        Log($"对端 {st.Name}",
                            $"仍为单方向（{(st.Outbound ? "出站" : "入站")}）。请确认对方版本一致、密钥相同，并在对方也执行一键修复。",
                            DesktopDiagnosticLevel.Warn);
                    else
                        Log($"对端 {st.Name}", "仍未连通。请确认对方已启动玄鸟且网卡/密钥一致。",
                            DesktopDiagnosticLevel.Error);
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
            Log("修复异常", ex.Message, DesktopDiagnosticLevel.Error);
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
}

public enum DesktopDiagnosticLevel
{
    Info,
    Ok,
    Warn,
    Error
}

public sealed class DesktopDiagnosticItem
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public DesktopDiagnosticLevel Level { get; init; }
}

public sealed class DesktopDiagnosticReport
{
    public string Summary { get; set; } = "";
    public List<DesktopDiagnosticItem> Items { get; } = [];

    public void Add(string title, string detail, DesktopDiagnosticLevel level)
        => Items.Add(new DesktopDiagnosticItem { Title = title, Detail = detail, Level = level });

    public string FormatText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary);
        sb.AppendLine();
        foreach (var i in Items)
        {
            var tag = i.Level switch
            {
                DesktopDiagnosticLevel.Ok => "✓",
                DesktopDiagnosticLevel.Warn => "!",
                DesktopDiagnosticLevel.Error => "✗",
                _ => "·"
            };
            sb.AppendLine($"{tag} [{i.Title}] {i.Detail}");
        }

        return sb.ToString().TrimEnd();
    }
}
