using System.Globalization;
using System.Text.RegularExpressions;
using FlyScreen.Services;

namespace FlyScreen.Services.Programs;

/// <summary>
/// 从源码注释块 <c>@fly-settings</c> … <c>@fly-settings-end</c> 解析可配项。
/// 支持各语言行注释：<c>//</c>（C#/JS）、<c>#</c>（Python/Bash/PowerShell）、
/// <c>rem</c>/<c>::</c>（Batch）。优先请用独立文件 <c>fly-settings.json</c>。
/// </summary>
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
            var t = StripLineComment(line);
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
            var filter = "";
            var eventName = "";
            ProgramTableSpec? table = null;

            switch (kind)
            {
                case ProgramSettingKind.Enum:
                case ProgramSettingKind.Radio:
                {
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
                    break;
                }
                case ProgramSettingKind.Checks:
                {
                    // selected | opt1,opt2  或仅 opt1,opt2（默认全不选）
                    var pipe = rest.IndexOf('|');
                    if (pipe >= 0)
                    {
                        defaultRaw = rest[..pipe].Trim().Replace(';', ',').Replace(',', '\n');
                        enumOpts = SplitCsv(rest[(pipe + 1)..]);
                    }
                    else
                    {
                        enumOpts = SplitCsv(rest);
                        defaultRaw = "";
                    }
                    break;
                }
                case ProgramSettingKind.StringList:
                    defaultRaw = rest.Replace(';', '\n').Replace(',', '\n');
                    break;
                case ProgramSettingKind.File:
                {
                    var (def, meta) = SplitDefaultAndMeta(rest);
                    defaultRaw = def;
                    filter = meta.GetValueOrDefault("filter")
                             ?? meta.GetValueOrDefault("ext")
                             ?? "";
                    break;
                }
                case ProgramSettingKind.Folder:
                {
                    var (def, _) = SplitDefaultAndMeta(rest);
                    defaultRaw = def;
                    break;
                }
                case ProgramSettingKind.Button:
                {
                    var (_, meta) = SplitDefaultAndMeta(rest);
                    eventName = meta.GetValueOrDefault("event")
                                ?? meta.GetValueOrDefault("action")
                                ?? key;
                    defaultRaw = "";
                    break;
                }
                case ProgramSettingKind.Table:
                {
                    var (_, meta) = SplitDefaultAndMeta(rest);
                    table = ParseTableSpec(meta);
                    defaultRaw = ProgramTableData.CreateEmpty(table.Rows, table.Cols).ToJson();
                    eventName = table.ButtonEvent;
                    break;
                }
            }

            fields.Add(new ProgramSettingField
            {
                Key = key,
                Kind = kind,
                Label = label,
                DefaultRaw = defaultRaw,
                EnumOptions = enumOpts,
                Filter = filter,
                EventName = eventName,
                Table = table
            });
        }

        return fields;
    }

    public static Dictionary<string, string> DefaultsMap(IReadOnlyList<ProgramSettingField> fields)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
        {
            if (f.Kind == ProgramSettingKind.Button)
                continue;
            map[f.Key] = NormalizeStored(f.Kind, f.DefaultRaw, f);
        }
        return map;
    }

    public static string NormalizeStored(ProgramSettingKind kind, string? raw)
        => NormalizeStored(kind, raw, field: null);

    public static string NormalizeStored(ProgramSettingKind kind, string? raw, ProgramSettingField? field)
    {
        raw ??= "";
        return kind switch
        {
            ProgramSettingKind.Bool =>
                bool.TryParse(raw.Trim(), out var b) ? (b ? "true" : "false")
                    : raw.Trim() is "1" or "yes" or "on" ? "true" : "false",
            ProgramSettingKind.StringList or ProgramSettingKind.Checks => string.Join("\n",
                raw.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            ProgramSettingKind.Int =>
                long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? ((int)Math.Clamp(i, int.MinValue, int.MaxValue)).ToString(CultureInfo.InvariantCulture)
                    : "0",
            ProgramSettingKind.Long =>
                long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : "0",
            ProgramSettingKind.Double =>
                double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString(CultureInfo.InvariantCulture)
                    : "0",
            ProgramSettingKind.Rect => NormRect.TryParse(raw)?.ToString() ?? "",
            ProgramSettingKind.Table =>
                ProgramTableData.ParseOrEmpty(raw, field?.Table).ToJson(),
            ProgramSettingKind.Button => "",
            ProgramSettingKind.File or ProgramSettingKind.Folder or ProgramSettingKind.String
                or ProgramSettingKind.Enum or ProgramSettingKind.Radio => raw.Trim(),
            _ => raw.Trim()
        };
    }

    public static bool AsBool(string? raw, bool fallback = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (bool.TryParse(raw, out var b)) return b;
        return raw.Trim() is "1" or "yes" or "on";
    }

    public static int AsInt(string? raw, int fallback = 0)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return (int)d;
        return fallback;
    }

    public static long AsLong(string? raw, long fallback = 0)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return (long)d;
        return fallback;
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

    public static ProgramTableData AsTable(string? raw, ProgramTableSpec? spec)
        => ProgramTableData.ParseOrEmpty(raw, spec);

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

    /// <summary>将 Windows 风格 filter（如 <c>*.txt;*.csv</c>）转为对话框用描述。</summary>
    public static string FormatFileFilter(string? filter, string fallback = "All|*.*")
    {
        if (string.IsNullOrWhiteSpace(filter)) return fallback;
        var parts = filter.Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return fallback;
        var pattern = string.Join(";", parts);
        return $"Files ({pattern})|{pattern}|All|*.*";
    }

    private static ProgramSettingKind MapKind(string typeTok)
    {
        var t = typeTok.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return t switch
        {
            "bool" or "boolean" => ProgramSettingKind.Bool,
            "string" or "text" or "str" => ProgramSettingKind.String,
            "string[]" or "strings" or "list" or "stringlist" => ProgramSettingKind.StringList,
            "enum" or "action" => ProgramSettingKind.Enum,
            "radio" or "radiogroup" or "radios" => ProgramSettingKind.Radio,
            "checks" or "checkboxgroup" or "checkboxes" or "multiselect" => ProgramSettingKind.Checks,
            "int" or "integer" => ProgramSettingKind.Int,
            "long" or "int64" => ProgramSettingKind.Long,
            "double" or "float" or "number" or "sec" => ProgramSettingKind.Double,
            "file" or "filepath" or "openfile" => ProgramSettingKind.File,
            "folder" or "directory" or "dir" or "path" => ProgramSettingKind.Folder,
            "button" or "btn" => ProgramSettingKind.Button,
            "table" or "grid" or "matrix" => ProgramSettingKind.Table,
            "rect" or "region" => ProgramSettingKind.Rect,
            _ when t.StartsWith("enum", StringComparison.Ordinal) => ProgramSettingKind.Enum,
            _ => ProgramSettingKind.String
        };
    }

    private static (string Default, Dictionary<string, string> Meta) SplitDefaultAndMeta(string rest)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rest))
            return ("", meta);

        // 支持：default | k=v;k=v   或整段全是 k=v;k=v
        string body;
        var pipe = rest.IndexOf('|');
        if (pipe >= 0)
        {
            var left = rest[..pipe].Trim();
            body = rest[(pipe + 1)..].Trim();
            if (!left.Contains('=', StringComparison.Ordinal))
            {
                ParseMeta(body, meta);
                return (left, meta);
            }
            // 左侧也像 meta：整段当 meta
            ParseMeta(rest.Replace('|', ';'), meta);
            return (meta.GetValueOrDefault("default") ?? "", meta);
        }

        if (rest.Contains('=', StringComparison.Ordinal))
        {
            ParseMeta(rest, meta);
            return (meta.GetValueOrDefault("default") ?? "", meta);
        }

        return (rest.Trim(), meta);
    }

    private static void ParseMeta(string s, Dictionary<string, string> meta)
    {
        foreach (var part in s.Split([';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var k = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();
            if (k.Length > 0)
                meta[k] = v;
        }
    }

    private static ProgramTableSpec ParseTableSpec(Dictionary<string, string> meta)
    {
        static int Num(Dictionary<string, string> m, string key, int fallback)
            => int.TryParse(m.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? Math.Clamp(n, 1, 50)
                : fallback;

        static bool Flag(Dictionary<string, string> m, string key)
        {
            if (!m.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return false;
            return AsBool(v, false) || string.Equals(v, "1", StringComparison.Ordinal);
        }

        var cols = Num(meta, "cols", 1);
        var rows = Num(meta, "rows", 1);
        var colNames = SplitCsv(meta.GetValueOrDefault("colNames")
                                ?? meta.GetValueOrDefault("columns")
                                ?? "");
        var rowNames = SplitCsv(meta.GetValueOrDefault("rowNames")
                                ?? meta.GetValueOrDefault("rowsNames")
                                ?? "");
        if (colNames.Count == 0)
            colNames = Enumerable.Range(1, cols).Select(i => "C" + i).ToList();
        if (rowNames.Count == 0)
            rowNames = Enumerable.Range(1, rows).Select(i => "R" + i).ToList();

        return new ProgramTableSpec
        {
            Cols = cols,
            Rows = rows,
            ColNames = colNames,
            RowNames = rowNames,
            RowHeader = Flag(meta, "rowHeader") || Flag(meta, "rowHeaders"),
            CellButton = Flag(meta, "cellButton") || Flag(meta, "cellButtons") || Flag(meta, "buttons"),
            ButtonEvent = meta.GetValueOrDefault("buttonEvent")
                          ?? meta.GetValueOrDefault("event")
                          ?? ""
        };
    }

    private static IReadOnlyList<string> SplitCsv(string s)
        => string.IsNullOrWhiteSpace(s)
            ? []
            : s.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>去掉各脚本语言的行注释前缀，得到可解析正文。</summary>
    public static string StripLineComment(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "";
        var t = line.Trim();
        if (t.StartsWith("//", StringComparison.Ordinal))
            return t[2..].Trim();
        if (t.StartsWith("::", StringComparison.Ordinal))
            return t[2..].Trim();
        if (t.StartsWith('#')
            && !t.StartsWith("#!", StringComparison.Ordinal)) // 保留 shebang 当空行跳过即可
            return t[1..].Trim();
        if (t.StartsWith("rem", StringComparison.OrdinalIgnoreCase)
            && (t.Length == 3 || char.IsWhiteSpace(t[3])))
            return t[3..].Trim();
        // 无前缀时也允许（独立粘贴的块）
        return t;
    }

    [GeneratedRegex(
        @"^(?<type>bool|boolean|string\[\]|strings|list|stringlist|string|text|str|enum|action|radio|radiogroup|radios|checks|checkboxgroup|checkboxes|multiselect|int|integer|long|int64|double|float|number|sec|file|filepath|openfile|folder|directory|dir|path|button|btn|table|grid|matrix|rect|region)\s+(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*\|\s*(?<label>[^|]+)\|(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineRegex();
}
