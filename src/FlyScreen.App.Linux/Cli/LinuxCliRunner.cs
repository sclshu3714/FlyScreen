using System.Runtime.InteropServices;
using FlyScreen.Linux;
using FlyScreen.Linux.Environment;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;

namespace FlyScreen.LinuxApp.Cli;

/// <summary>命令行 / Daemon：与 GUI 共用 <see cref="LinuxAppHost"/>。</summary>
public static class LinuxCliRunner
{
    public static async Task<int> RunAsync(string[] args, bool daemon)
    {
        var host = LinuxAppHost.EnsureCreated();
        host.StatusMessage += msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

        ApplyCliOverrides(host, args);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var signalRegs = new List<IDisposable>();
        try
        {
            signalRegs.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cts.Cancel()));
            signalRegs.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => cts.Cancel()));
        }
        catch
        {
            // 非 POSIX 平台忽略
        }

        await host.StartAsync().ConfigureAwait(false);

        if (HasFlag(args, "--status") && !daemon)
        {
            PrintStatus(host);
            await host.DisposeAsync().ConfigureAwait(false);
            return 0;
        }

        if (daemon)
        {
            Console.WriteLine("Daemon 运行中（SIGTERM/Ctrl+C 退出）…");
            try { await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* shutdown */ }
        }
        else
        {
            Console.WriteLine("命令: status | peers | send | inbox | clip | save | quit | help");
            await InteractiveLoopAsync(host, cts.Token).ConfigureAwait(false);
        }

        foreach (var r in signalRegs) r.Dispose();
        await host.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    private static void ApplyCliOverrides(LinuxAppHost host, string[] args)
    {
        var key = GetOpt(args, "--key=");
        var slots = host.Settings.MachineSlots.ToList();
        while (slots.Count < 4) slots.Add("");

        foreach (var a in args)
        {
            if (!a.StartsWith("--slot=", StringComparison.OrdinalIgnoreCase)) continue;
            var name = a["--slot=".Length..].Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var idx = slots.FindIndex(string.IsNullOrWhiteSpace);
            if (idx < 0) idx = 1;
            if (idx >= 0 && idx < slots.Count)
                slots[idx] = name;
            else
                slots.Add(name);
        }

        if (!string.IsNullOrEmpty(key) || args.Any(a => a.StartsWith("--slot=", StringComparison.OrdinalIgnoreCase)))
        {
            host.ApplySettingsFrom(key ?? host.Settings.SecurityKey, slots);
            Console.WriteLine("设置已保存。");
        }
    }

    private static async Task InteractiveLoopAsync(LinuxAppHost host, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line == null) break;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var parts = SplitArgs(trimmed);
            var cmd = parts[0].ToLowerInvariant();
            try
            {
                switch (cmd)
                {
                    case "quit":
                    case "exit":
                    case "q":
                        return;
                    case "status":
                    case "s":
                        PrintStatus(host);
                        break;
                    case "peers":
                    case "p":
                        PrintPeers(host);
                        break;
                    case "send":
                        await CmdSendAsync(host, parts).ConfigureAwait(false);
                        break;
                    case "inbox":
                        CmdInbox(host);
                        break;
                    case "clip":
                        await CmdClipAsync(host, parts).ConfigureAwait(false);
                        break;
                    case "save":
                        host.Settings.Save();
                        Console.WriteLine("已保存 settings.json");
                        break;
                    case "help":
                    case "?":
                        PrintHelp();
                        break;
                    default:
                        Console.WriteLine($"未知命令: {parts[0]}（输入 help）");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"错误: {ex.Message}");
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            status | peers | save | quit
            inbox                          显示收件箱路径与最近文件
            send <peer> <path> [destDir]   直传到对端目录（省略 dest 则用对端 Inbox）
            clip set <text>                向已连接对端宣告文本
            clip set -                     从 stdin 读一行文本并宣告
            （命令模式不做系统级富文本/文件粘贴，收到文件写入 Inbox）
            """);
    }

    private static async Task CmdSendAsync(LinuxAppHost host, string[] parts)
    {
        if (parts.Length < 3)
        {
            Console.WriteLine("用法: send <peer> <path> [destDir]");
            return;
        }

        if (!host.Settings.TransferFile)
        {
            Console.WriteLine("设置中未启用 TransferFile，请在 settings 中打开或 GUI 勾选「启用文件传输」。");
            return;
        }

        var peer = parts[1];
        var path = parts[2];
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Console.WriteLine($"路径不存在: {path}");
            return;
        }

        if (!host.Connections.IsPeerReachable(peer))
        {
            Console.WriteLine($"对端不可达: {peer}");
            return;
        }

        var dest = parts.Length >= 4 ? parts[3] : "";
        if (string.IsNullOrWhiteSpace(dest))
        {
            var folders = await host.Transfers.QueryRemoteFoldersAsync(peer).ConfigureAwait(false);
            dest = folders?.Inbox ?? "";
            if (string.IsNullOrWhiteSpace(dest))
            {
                Console.WriteLine("无法解析对端 Inbox，请显式指定 destDir。");
                return;
            }
        }

        Console.WriteLine($"正在发送 → {peer}:{dest} …");
        await host.Transfers.StartDirectTransferAsync(peer, dest, [path]).ConfigureAwait(false);
        Console.WriteLine("已提交传输（见 status 或对端 Inbox）。");
    }

    private static void CmdInbox(LinuxAppHost host)
    {
        var dir = host.Transfers.InboxDirectory;
        Directory.CreateDirectory(dir);
        Console.WriteLine($"Inbox: {dir}");
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir).OrderByDescending(File.GetLastWriteTimeUtc).Take(10).ToArray()
            : [];
        if (files.Length == 0)
        {
            Console.WriteLine("(空)");
            return;
        }

        foreach (var f in files)
            Console.WriteLine($"  {Path.GetFileName(f)}  ({new FileInfo(f).Length} bytes)");
    }

    private static async Task CmdClipAsync(LinuxAppHost host, string[] parts)
    {
        if (parts.Length < 2 || !string.Equals(parts[1], "set", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("用法: clip set <text> | clip set -");
            return;
        }

        if (!host.Settings.ShareClipboard)
        {
            Console.WriteLine("设置中未启用 ShareClipboard。");
            return;
        }

        string text;
        if (parts.Length == 2 || parts[2] == "-")
        {
            Console.Write("文本> ");
            text = await Console.In.ReadLineAsync().ConfigureAwait(false) ?? "";
        }
        else
            text = string.Join(' ', parts.Skip(2));

        if (string.IsNullOrEmpty(text))
        {
            Console.WriteLine("空文本，已取消。");
            return;
        }

        await host.Transfers.AnnounceLocalInlineAsync(new ClipboardInlineData { Text = text })
            .ConfigureAwait(false);
        Console.WriteLine($"已宣告文本（{text.Length} 字符）到可达对端。");
    }

    private static void PrintStatus(LinuxAppHost host)
    {
        Console.WriteLine($"本机: {LocalPeerIdentity.MachineName}（系统名={Environment.MachineName}）");
        Console.WriteLine(LinuxRuntimeEnvironment.Describe());
        Console.WriteLine($"Caps: {string.Join(", ", LocalPeerIdentity.Caps)}");
        Console.WriteLine($"绑定: {host.Connections.BindIp}:{host.Settings.TcpPort} UDP={host.Settings.UdpPort}");
        Console.WriteLine($"注入: {host.Injector.BackendName} available={host.Injector.IsAvailable}");
        Console.WriteLine(host.Connections.IsListening
            ? $"监听 {host.Connections.BindIp}:{host.Settings.TcpPort} UDP={host.Settings.UdpPort}"
            : "未监听");
        Console.WriteLine($"密钥长度: {host.Settings.SecurityKey.Length}");
        Console.WriteLine($"ShareClipboard={host.Settings.ShareClipboard} TransferFile={host.Settings.TransferFile}");
        Console.WriteLine($"Inbox={host.Transfers.InboxDirectory}");
        PrintPeers(host);
    }

    private static void PrintPeers(LinuxAppHost host)
    {
        var discovered = host.Connections.Discovery.GetPeers();
        Console.WriteLine("发现: " + (discovered.Count == 0
            ? "(无)"
            : string.Join(", ", discovered.Select(p => $"{p.MachineName}@{p.Ip}"))));
        foreach (var st in host.Connections.GetConfiguredPeerStatuses())
        {
            var state = st.Bidirectional ? "双向" : st.AnyAlive ? "单向" : "未连接";
            Console.WriteLine($"  配置 {st.Name}: {state}");
        }
    }

    private static string[] SplitArgs(string line)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        var inQuote = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (!inQuote && char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0)
                {
                    list.Add(cur.ToString());
                    cur.Clear();
                }

                continue;
            }

            cur.Append(ch);
        }

        if (cur.Length > 0) list.Add(cur.ToString());
        return list.ToArray();
    }

    private static string? GetOpt(string[] args, string prefix)
        => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?
            [prefix.Length..];

    private static bool HasFlag(string[] args, params string[] names)
        => args.Any(a => names.Any(n => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)));
}
