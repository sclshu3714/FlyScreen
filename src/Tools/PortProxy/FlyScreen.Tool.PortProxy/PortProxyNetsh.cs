using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace FlyScreen.Tool.PortProxy;

/// <summary>封装 <c>netsh interface portproxy</c> 全部可操作命令（Windows 专版）。</summary>
internal static class PortProxyNetsh
{
    private static readonly Regex DumpAddLine = new(
        @"^\s*add\s+(?<kind>v4tov4|v4tov6|v6tov4|v6tov6)\b(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NamedArg = new(
        @"(?<key>listenaddress|listenport|connectaddress|connectport|protocol)\s*=\s*(?<val>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DataLine = new(
        @"^\s*(?<la>\S+)\s+(?<lp>\d{1,5})\s+(?<ca>\S+)\s+(?<cp>\d{1,5})\s*$",
        RegexOptions.Compiled);

    private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();

    static PortProxyNetsh()
    {
        // .NET 默认不含 GBK/936 等代码页；中文 Windows 的 netsh 输出依赖 OEM/ANSI
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static Encoding ResolveConsoleEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            var oem = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            if (oem > 0)
                return Encoding.GetEncoding(oem);
        }
        catch { /* fall through */ }

        try
        {
            var ansi = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (ansi > 0)
                return Encoding.GetEncoding(ansi);
        }
        catch { /* fall through */ }

        try { return Encoding.GetEncoding(936); }
        catch { return Encoding.Default; }
    }

    public static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<PortProxyRule>> ShowAsync(
        PortProxyKind kind, CancellationToken ct = default)
    {
        var r = await RunAsync($"interface portproxy show {kind.ToNetshToken()}", elevate: false, ct)
            .ConfigureAwait(false);
        return ParseShowOutput(kind, r.StdOut);
    }

    public static async Task<IReadOnlyList<PortProxyRule>> ShowAllAsync(CancellationToken ct = default)
    {
        var list = new List<PortProxyRule>();
        foreach (var kind in PortProxyKindExtensions.All)
        {
            ct.ThrowIfCancellationRequested();
            list.AddRange(await ShowAsync(kind, ct).ConfigureAwait(false));
        }

        return list;
    }

    public static Task<PortProxyCommandResult> AddAsync(PortProxyRule rule, bool elevateIfNeeded, CancellationToken ct = default)
        => MutateAsync("add", rule, includeConnect: true, elevateIfNeeded, ct);

    public static Task<PortProxyCommandResult> SetAsync(PortProxyRule rule, bool elevateIfNeeded, CancellationToken ct = default)
        => MutateAsync("set", rule, includeConnect: true, elevateIfNeeded, ct);

    public static async Task<PortProxyCommandResult> DeleteAsync(
        PortProxyRule rule, bool elevateIfNeeded, CancellationToken ct = default)
    {
        var args = new StringBuilder();
        args.Append("interface portproxy delete ").Append(rule.Kind.ToNetshToken());
        args.Append(" listenport=").Append(Quote(rule.ListenPort));
        if (!string.IsNullOrWhiteSpace(rule.ListenAddress))
            args.Append(" listenaddress=").Append(Quote(rule.ListenAddress));
        if (!string.IsNullOrWhiteSpace(rule.Protocol))
            args.Append(" protocol=").Append(Quote(rule.Protocol));
        return await RunAsync(args.ToString(), elevateIfNeeded, ct).ConfigureAwait(false);
    }

    public static Task<PortProxyCommandResult> ResetAsync(bool elevateIfNeeded, CancellationToken ct = default)
        => RunAsync("interface portproxy reset", elevateIfNeeded, ct);

    public static async Task<PortProxyCommandResult> DumpAsync(CancellationToken ct = default)
    {
        var r = await RunAsync("interface portproxy dump", elevate: false, ct).ConfigureAwait(false);
        if (r.Ok && string.IsNullOrWhiteSpace(r.Message))
            return PortProxyCommandResult.Success(r.StdOut, r.StdOut);
        return r;
    }

    public static IReadOnlyList<PortProxyRule> ParseDump(string dumpText)
    {
        var list = new List<PortProxyRule>();
        if (string.IsNullOrWhiteSpace(dumpText)) return list;

        foreach (var raw in dumpText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = DumpAddLine.Match(raw);
            if (!m.Success) continue;
            if (!PortProxyKindExtensions.TryParse(m.Groups["kind"].Value, out var kind))
                continue;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in NamedArg.Matches(m.Groups["rest"].Value))
                map[a.Groups["key"].Value] = a.Groups["val"].Value;

            map.TryGetValue("listenaddress", out var la);
            map.TryGetValue("listenport", out var lp);
            map.TryGetValue("connectaddress", out var ca);
            map.TryGetValue("connectport", out var cp);
            map.TryGetValue("protocol", out var proto);

            if (string.IsNullOrWhiteSpace(lp)) continue;
            list.Add(new PortProxyRule
            {
                Kind = kind,
                ListenAddress = la ?? "",
                ListenPort = lp,
                ConnectAddress = ca ?? "",
                ConnectPort = cp ?? "",
                Protocol = string.IsNullOrWhiteSpace(proto) ? "tcp" : proto
            });
        }

        return list;
    }

    public static IReadOnlyList<PortProxyRule> ParseShowOutput(PortProxyKind kind, string stdout)
    {
        var list = new List<PortProxyRule>();
        if (string.IsNullOrWhiteSpace(stdout)) return list;

        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('-') || line.Contains("---", StringComparison.Ordinal)) continue;
            if (ContainsAny(line, "listen", "connect", "address", "port", "侦听", "监听", "连接", "地址", "端口", "ipv4", "ipv6"))
            {
                // 表头行：若同时含数字端口列则仍可能是数据；仅跳过明显表头
                if (!DataLine.IsMatch(line))
                    continue;
            }

            var m = DataLine.Match(line);
            if (!m.Success) continue;

            list.Add(new PortProxyRule
            {
                Kind = kind,
                ListenAddress = m.Groups["la"].Value,
                ListenPort = m.Groups["lp"].Value,
                ConnectAddress = m.Groups["ca"].Value,
                ConnectPort = m.Groups["cp"].Value,
                Protocol = "tcp"
            });
        }

        return list;
    }

    private static async Task<PortProxyCommandResult> MutateAsync(
        string verb, PortProxyRule rule, bool includeConnect, bool elevateIfNeeded, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rule.ListenPort))
            return PortProxyCommandResult.Fail("listenport 不能为空");
        if (includeConnect && string.IsNullOrWhiteSpace(rule.ConnectAddress))
            return PortProxyCommandResult.Fail("connectaddress 不能为空");

        var args = new StringBuilder();
        args.Append("interface portproxy ").Append(verb).Append(' ').Append(rule.Kind.ToNetshToken());
        args.Append(" listenport=").Append(Quote(rule.ListenPort));
        if (!string.IsNullOrWhiteSpace(rule.ListenAddress))
            args.Append(" listenaddress=").Append(Quote(rule.ListenAddress));
        if (includeConnect)
        {
            args.Append(" connectaddress=").Append(Quote(rule.ConnectAddress));
            if (!string.IsNullOrWhiteSpace(rule.ConnectPort))
                args.Append(" connectport=").Append(Quote(rule.ConnectPort));
        }

        if (!string.IsNullOrWhiteSpace(rule.Protocol))
            args.Append(" protocol=").Append(Quote(rule.Protocol));

        return await RunAsync(args.ToString(), elevateIfNeeded, ct).ConfigureAwait(false);
    }

    private static async Task<PortProxyCommandResult> RunAsync(string arguments, bool elevate, CancellationToken ct)
    {
        try
        {
            var needElevate = elevate && !IsProcessElevated();
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = needElevate,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (needElevate)
            {
                // UseShellExecute + runas：无法重定向输出，以 ExitCode / UAC 取消为准
                psi.Verb = "runas";
            }
            else
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                // 中文系统 netsh 多为 OEM/ANSI（如 936）；需已 RegisterProvider
                psi.StandardOutputEncoding = ConsoleEncoding;
                psi.StandardErrorEncoding = ConsoleEncoding;
            }

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
                return PortProxyCommandResult.Fail("无法启动 netsh");

            string stdout = "", stderr = "";
            if (!needElevate)
            {
                var outTask = proc.StandardOutput.ReadToEndAsync(ct);
                var errTask = proc.StandardError.ReadToEndAsync(ct);
                await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
                stdout = outTask.Result;
                stderr = errTask.Result;
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var code = proc.ExitCode;
            var combined = (stdout + "\n" + stderr).Trim();

            if (code != 0)
            {
                var msg = string.IsNullOrWhiteSpace(combined)
                    ? (needElevate
                        ? "操作失败或已取消 UAC 提升（修改 portproxy 通常需要管理员权限）"
                        : $"netsh 退出码 {code}")
                    : combined;
                return PortProxyCommandResult.Fail(msg, code, stdout, stderr);
            }

            return PortProxyCommandResult.Success(
                string.IsNullOrWhiteSpace(combined) ? "完成" : combined,
                stdout);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return PortProxyCommandResult.Fail("已取消管理员授权（UAC）", -1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PortProxyCommandResult.Fail(ex.Message);
        }
    }

    private static string Quote(string value)
    {
        var v = value.Trim();
        if (v.Contains(' ') || v.Contains('"'))
            return "\"" + v.Replace("\"", "\\\"") + "\"";
        return v;
    }

    private static bool ContainsAny(string hay, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (hay.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
