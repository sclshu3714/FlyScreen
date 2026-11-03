using System.Globalization;
using System.Text.RegularExpressions;
using FlyScreen.Services;

namespace FlyScreen.Services.Programs;

/// <summary>从源码中的 <c>// @fly-settings</c> … <c>// @fly-settings-end</c> 解析可配项。</summary>
public static partial class ProgramSettingsSchema
{
    private static readonly Regex LineRx = LineRegex();

    public static IReadOnlyList<ProgramSettingField> Parse(string? source)
    {
        if (string.IsNullOrEmpty(source))
            return [];

        var start = source.IndexOf("@fly-settings", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return [];
        var end = source.IndexOf("@fly-settings-end", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = source.Length;

        var block = source[start..end];
        var fields = new List<ProgramSettingField>();
        using var reader = new StringReader(block);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var t = line.Trim();
            if (t.StartsWith("//", StringComparison.Ordinal))
                t = t[2..].Trim();
            if (t.Length == 0
                || t.StartsWith("@fly-settings", StringComparison.OrdinalIgnoreCase))
                continue;

            var m = LineRx.Match(t);
            if (!m.Success) continue;

            var typeTok = m.Groups["type"].Value.Trim();
            var key = m.Groups["key"].Value.Trim();
            var label = m.Groups["label"].Value.Trim();
            var rest = m.Groups["rest"].Value.Trim();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(label))
                continue;

            var kind = MapKind(typeTok);
            string defaultRaw = rest;
            IReadOnlyList<string> enumOpts = [];
            if (kind == ProgramSettingKind.Enum)
            {
                // default | opt1,opt2,...   或仅 opt1,opt2（第一项为默认）
                var pipe = rest.IndexOf('|');
                if (pipe >= 0)
                {
                    defaultRaw = rest[..pipe].Trim();
                    enumOpts = SplitCsv(rest[(pipe + 1)..]);
                }
                else
                {
                    enumOpts = SplitCsv(rest);
                    defaultRaw = enumOpts.Count > 0 ? enumOpts[0] : "";
                }
            }
            else if (kind == ProgramSettingKind.StringList)
            {
                // 默认：逗号或分号分隔，设置页用换行编辑
                defaultRaw = rest.Replace(';', '\n').Replace(',', '\n');
            }

            fields.Add(new ProgramSettingField
            {
                Key = key,
                Kind = kind,
                Label = label,
                DefaultRaw = defaultRaw,
                EnumOptions = enumOpts
            });
        }

        return fields;
    }

    public static Dictionary<string, string> DefaultsMap(IReadOnlyList<ProgramSettingField> fields)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
            map[f.Key] = NormalizeStored(f.Kind, f.DefaultRaw);
        return map;
    }

    public static string NormalizeStored(ProgramSettingKind kind, string? raw)
    {
        raw ??= "";
        return kind switch
        {
            ProgramSettingKind.Bool =>
                bool.TryParse(raw.Trim(), out var b) ? (b ? "true" : "false")
                    : raw.Trim() is "1" or "yes" or "on" ? "true" : "false",
            ProgramSettingKind.StringList => string.Join("\n",
                raw.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            ProgramSettingKind.Double =>
                double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString(CultureInfo.InvariantCulture)
                    : "0",
            ProgramSettingKind.Rect => NormRect.TryParse(raw)?.ToString() ?? "",
            _ => raw.Trim()
        };
    }

    public static bool AsBool(string? raw, bool fallback = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (bool.TryParse(raw, out var b)) return b;
        return raw.Trim() is "1" or "yes" or "on";
    }

    public static double AsDouble(string? raw, double fallback = 0)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return fallback;
    }

    public static IReadOnlyList<string> AsStringList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static ProgramHitAction AsAction(string? raw, ProgramHitAction fallback = ProgramHitAction.SwipeUp)
    {
        if (Enum.TryParse<ProgramHitAction>(raw?.Trim(), ignoreCase: true, out var a))
            return a;
        return fallback;
    }

    /// <summary>
    /// 解析程序「日志级别」设置：支持 Error/Warn/Info/Debug 及中文 错误/警告/信息/调试。
    /// </summary>
    public static AppLogLevel AsLogLevel(string? raw, AppLogLevel fallback = AppLogLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var t = raw.Trim();
        if (Enum.TryParse<AppLogLevel>(t, ignoreCase: true, out var lv)
            && lv is AppLogLevel.Debug or AppLogLevel.Info or AppLogLevel.Warn or AppLogLevel.Error)
            return lv;
        return t switch
        {
            "错误" or "error" or "err" => AppLogLevel.Error,
            "警告" or "warn" or "warning" => AppLogLevel.Warn,
            "信息" or "info" or "information" or "消息" => AppLogLevel.Info,
            "调试" or "debug" or "dbg" or "verbose" => AppLogLevel.Debug,
            _ => fallback
        };
    }

    /// <summary>
    /// 从 settings 解析最低写入级别：优先 <c>logLevel</c>；旧版 <c>debugLog=true</c> 视为 Debug。
    /// </summary>
    public static AppLogLevel ResolveLogLevel(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings == null) return AppLogLevel.Info;
        settings.TryGetValue("logLevel", out var rawLevel);
        var hasExplicit = !string.IsNullOrWhiteSpace(rawLevel);
        var level = AsLogLevel(rawLevel, AppLogLevel.Info);
        if (!hasExplicit
            && settings.TryGetValue("debugLog", out var dbg)
            && AsBool(dbg, false))
            return AppLogLevel.Debug;
        return level;
    }

    private static ProgramSettingKind MapKind(string typeTok)
    {
        var t = typeTok.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return t switch
        {
            "bool" or "boolean" => ProgramSettingKind.Bool,
            "string[]" or "strings" or "list" or "stringlist" => ProgramSettingKind.StringList,
            "enum" or "action" => ProgramSettingKind.Enum,
            "double" or "float" or "number" or "sec" => ProgramSettingKind.Double,
            "rect" or "region" => ProgramSettingKind.Rect,
            _ when t.StartsWith("enum", StringComparison.Ordinal) => ProgramSettingKind.Enum,
            _ => ProgramSettingKind.Bool
        };
    }

    private static IReadOnlyList<string> SplitCsv(string s)
        => s.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(
        @"^(?<type>bool|boolean|string\[\]|strings|list|stringlist|enum|action|double|float|number|sec|rect|region)\s+(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*\|\s*(?<label>[^|]+)\|(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineRegex();
}
