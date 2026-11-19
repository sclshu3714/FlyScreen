using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FlyScreen.Services.Programs;

/// <summary>
/// 桌面 CLI 语言运行时辅助：为 PowerShell 注入 <c>Fly.*</c> 设置读取，并把 Write-Host 等合流到 stdout。
/// </summary>
internal static class ProgramCliFlyBridge
{
    private static readonly Regex FlyCall = new(
        @"\bFly\.([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex FlyMember = new(
        @"\bFly\.([A-Za-z_][A-Za-z0-9_]*)\b(?!\s*\()",
        RegexOptions.Compiled);

    /// <summary>
    /// 生成可执行的临时 runner.ps1（含 Fly 类型 + 改写后的用户脚本），返回路径。
    /// 调用方负责在进程结束后删除。
    /// </summary>
    public static string CreatePowerShellRunner(
        string userScriptPath,
        IReadOnlyDictionary<string, string> settings)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlyScreen", "ps-run");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N");
        var rewrittenPath = Path.Combine(dir, id + ".user.ps1");
        var runnerPath = Path.Combine(dir, id + ".runner.ps1");

        var raw = File.ReadAllText(userScriptPath, Encoding.UTF8);
        var rewritten = RewriteFlyMembers(raw);
        File.WriteAllText(rewrittenPath, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var runner = BuildRunnerScript(rewrittenPath, settings ?? new Dictionary<string, string>());
        File.WriteAllText(runnerPath, runner, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return runnerPath;
    }

    public static void TryDeleteRunnerArtifacts(string? runnerPath)
    {
        if (string.IsNullOrWhiteSpace(runnerPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(runnerPath);
            var name = Path.GetFileNameWithoutExtension(runnerPath); // {id}.runner
            if (dir == null || string.IsNullOrEmpty(name)) return;
            var id = name.EndsWith(".runner", StringComparison.OrdinalIgnoreCase)
                ? name[..^".runner".Length]
                : name;
            foreach (var f in new[]
                     {
                         Path.Combine(dir, id + ".runner.ps1"),
                         Path.Combine(dir, id + ".user.ps1")
                     })
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>将 <c>Fly.Foo(</c> / <c>Fly.Bar</c> 改为 PowerShell 静态成员 <c>[Fly]::Foo(</c> / <c>[Fly]::Bar</c>。</summary>
    internal static string RewriteFlyMembers(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;
        var s = FlyCall.Replace(source, "[Fly]::$1(");
        s = FlyMember.Replace(s, "[Fly]::$1");
        return s;
    }

    private static string BuildRunnerScript(
        string userScriptPath,
        IReadOnlyDictionary<string, string> settings)
    {
        static string Q(string p) => "'" + (p ?? "").Replace("'", "''", StringComparison.Ordinal) + "'";

        var init = new StringBuilder();
        foreach (var kv in settings)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            init.Append("                    S[")
                .Append(CsString(kv.Key.Trim()))
                .Append("] = ")
                .Append(CsString(kv.Value ?? ""))
                .AppendLine(";");
        }

        return
            $$"""
            $ErrorActionPreference = 'Continue'
            try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
            try { $OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

            Add-Type -Language CSharp -TypeDefinition @"
            using System;
            using System.Collections.Generic;
            using System.Globalization;
            public static class Fly
            {
                static readonly Dictionary<string, string> S =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                static Fly()
                {
            {{init}}
                }
                static string Raw(string key)
                {
                    if (string.IsNullOrWhiteSpace(key)) return null;
                    return S.TryGetValue(key.Trim(), out var v) ? v : null;
                }
                public static string GetString(string key, string fallback)
                {
                    if (fallback == null) fallback = "";
                    var v = Raw(key);
                    return string.IsNullOrEmpty(v) ? fallback : v;
                }
                public static string GetString(string key) { return GetString(key, ""); }
                public static bool GetBool(string key, bool fallback)
                {
                    var v = Raw(key);
                    if (string.IsNullOrWhiteSpace(v)) return fallback;
                    if (bool.TryParse(v, out var b)) return b;
                    if (v == "1" || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return true;
                    if (v == "0" || string.Equals(v, "no", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) return false;
                    return fallback;
                }
                public static bool GetBool(string key) { return GetBool(key, false); }
                public static int GetInt(string key, int fallback)
                {
                    var v = Raw(key);
                    return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
                }
                public static int GetInt(string key) { return GetInt(key, 0); }
                public static long GetLong(string key, long fallback)
                {
                    var v = Raw(key);
                    return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
                }
                public static long GetLong(string key) { return GetLong(key, 0); }
                public static double GetDouble(string key, double fallback)
                {
                    var v = Raw(key);
                    return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
                }
                public static double GetDouble(string key) { return GetDouble(key, 0); }
                public static string[] GetStringList(string key)
                {
                    var v = Raw(key);
                    if (string.IsNullOrWhiteSpace(v)) return new string[0];
                    var parts = v.Split(new[] { ',', ';', '|', '\n', '\r' },
                        StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
                    return parts;
                }
            }
            "@

            function Write-Host {
                param(
                    [Parameter(ValueFromPipeline = $true, ValueFromRemainingArguments = $true)]
                    [object[]] $Object,
                    [ConsoleColor] $ForegroundColor,
                    [ConsoleColor] $BackgroundColor,
                    [switch] $NoNewline,
                    [object] $Separator = ' '
                )
                $text = ($Object | ForEach-Object { "$_" }) -join [string]$Separator
                if ($NoNewline) { [Console]::Out.Write($text) }
                else { [Console]::Out.WriteLine($text) }
            }

            $__flyUser = {{Q(userScriptPath)}}
            $__flyExit = 0
            try {
                & {
                    . $__flyUser
                } *>&1 | ForEach-Object {
                    if ($_ -is [System.Management.Automation.ErrorRecord]) {
                        [Console]::Error.WriteLine($_.Exception.Message)
                        if ($_.InvocationInfo -and $_.InvocationInfo.PositionMessage) {
                            [Console]::Error.WriteLine($_.InvocationInfo.PositionMessage)
                        }
                    }
                    else {
                        [Console]::Out.WriteLine("$_")
                    }
                }
                if ($null -ne $LASTEXITCODE) { $__flyExit = [int]$LASTEXITCODE }
            }
            catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                $__flyExit = 1
            }
            exit $__flyExit
            """;
    }

    private static string CsString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append(@"\r"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (ch < 32)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
