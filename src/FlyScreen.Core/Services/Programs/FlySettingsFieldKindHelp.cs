using System.Text;
using System.Text.Json;

namespace FlyScreen.Services.Programs;

/// <summary>字段编辑弹窗右侧：类型说明与取值示例（三端共用文案）。</summary>
public static class FlySettingsFieldKindHelp
{
    public sealed record Preview(string Description, string Example);

    public static Preview Build(FlySettingsFieldEdit draft)
    {
        draft ??= new FlySettingsFieldEdit();
        var key = string.IsNullOrWhiteSpace(draft.Key) ? "field1" : draft.Key.Trim();
        var label = string.IsNullOrWhiteSpace(draft.Label) ? "字段" : draft.Label.Trim();
        var kind = draft.Kind;
        var def = (draft.DefaultRaw ?? "").Trim();
        var options = SplitList(draft.EnumOptionsText);
        if (options.Count == 0 && kind is ProgramSettingKind.Enum or ProgramSettingKind.Radio or ProgramSettingKind.Checks)
            options = ["选项A", "选项B", "选项C"];

        var desc = BuildDescription(kind);
        var example = BuildExample(kind, key, label, def, options,
            draft.Filter ?? "", draft.EventName ?? "",
            draft.TableCols, draft.TableRows,
            SplitList(draft.ColNamesText), SplitList(draft.RowNamesText),
            draft.RowHeader, draft.CellButton, draft.ButtonEvent ?? "");
        return new Preview(desc, example);
    }

    private static string BuildDescription(ProgramSettingKind kind) => kind switch
    {
        ProgramSettingKind.Bool =>
            "Bool：开关。settings.json 存 true/false；脚本用 Fly.GetBool(key) 读取。",
        ProgramSettingKind.String =>
            "String：单行文本。默认值即初始字符串；脚本用 Fly.GetString(key)。",
        ProgramSettingKind.StringList =>
            "StringList：多值列表。默认值与取值用逗号分隔；脚本用 Fly.GetStringList(key)。",
        ProgramSettingKind.Enum =>
            "Enum：下拉单选。须填写「选项」；默认值应为其中一个选项；Fly.GetString / GetAction。",
        ProgramSettingKind.Radio =>
            "Radio：单选组。须填写「选项」；用户只能选一项，写入字符串；Fly.GetString(key)。",
        ProgramSettingKind.Checks =>
            "Checks：复选组。须填写「选项」；取值可为多个选项（逗号分隔）；Fly.GetStringList(key)。",
        ProgramSettingKind.Int =>
            "Int：32 位整数。默认值写数字；脚本用 Fly.GetInt(key)。",
        ProgramSettingKind.Long =>
            "Long：64 位整数。默认值写数字；脚本用 Fly.GetLong(key)。",
        ProgramSettingKind.Double =>
            "Double：浮点数。默认值用小数点；脚本用 Fly.GetDouble(key)。",
        ProgramSettingKind.File =>
            "File：文件选择。可选「文件过滤器」（如 *.json;*.txt）；取值是路径字符串；Fly.GetString(key)。",
        ProgramSettingKind.Folder =>
            "Folder：文件夹选择。默认值可为初始路径；取值是路径字符串；Fly.GetString(key)。",
        ProgramSettingKind.Button =>
            "Button：按钮。填写「事件名」；点击后入队，脚本用 Fly.TryConsumeSettingEvent 消费（无持久取值）。",
        ProgramSettingKind.Table =>
            "Table：表格。配置列/行与可选行头、单元格按钮；脚本用 Fly.GetTable(key)；单元格按钮 detail 为 row,col（0 基）。",
        ProgramSettingKind.Rect =>
            "Rect：归一化区域 x,y,w,h（0..1）。通常由手机标定写入；脚本用 Fly.GetRect(key)。本对话框不填默认值。",
        _ => kind + "：见程序设置文档。"
    };

    private static string BuildExample(
        ProgramSettingKind kind,
        string key,
        string label,
        string def,
        IReadOnlyList<string> options,
        string filter,
        string eventName,
        int cols,
        int rows,
        IReadOnlyList<string> colNames,
        IReadOnlyList<string> rowNames,
        bool rowHeader,
        bool cellButton,
        string buttonEvent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【fly-settings.json 片段】");
        sb.AppendLine(BuildSchemaSnippet(kind, key, label, def, options, filter, eventName,
            cols, rows, colNames, rowNames, rowHeader, cellButton, buttonEvent));
        sb.AppendLine();
        sb.AppendLine("【settings.json 取值】");
        sb.AppendLine(BuildSettingsValue(kind, key, def, options));
        sb.AppendLine();
        sb.AppendLine("【脚本读取】");
        sb.Append(BuildApiHint(kind, key, eventName, buttonEvent));
        return sb.ToString().TrimEnd();
    }

    private static string BuildSchemaSnippet(
        ProgramSettingKind kind,
        string key,
        string label,
        string def,
        IReadOnlyList<string> options,
        string filter,
        string eventName,
        int cols,
        int rows,
        IReadOnlyList<string> colNames,
        IReadOnlyList<string> rowNames,
        bool rowHeader,
        bool cellButton,
        string buttonEvent)
    {
        // 用缩进字符串拼可读 JSON，避免依赖字段 DTO 序列化细节
        var lines = new List<string>
        {
            "{",
            $"  \"key\": {Q(key)},",
            $"  \"kind\": {Q(kind.ToString())},",
            $"  \"label\": {Q(label)},"
        };

        switch (kind)
        {
            case ProgramSettingKind.Button:
                lines.Add($"  \"eventName\": {Q(string.IsNullOrWhiteSpace(eventName) ? "Reset" : eventName)}");
                break;
            case ProgramSettingKind.Table:
                cols = Math.Clamp(cols <= 0 ? 2 : cols, 1, 20);
                rows = Math.Clamp(rows <= 0 ? 2 : rows, 1, 50);
                if (colNames.Count == 0) colNames = Enumerable.Range(0, cols).Select(i => $"C{i + 1}").ToList();
                if (rowNames.Count == 0) rowNames = Enumerable.Range(0, rows).Select(i => $"R{i + 1}").ToList();
                var be = string.IsNullOrWhiteSpace(buttonEvent)
                    ? (string.IsNullOrWhiteSpace(eventName) ? "CellClick" : eventName)
                    : buttonEvent;
                lines.Add("  \"table\": {");
                lines.Add($"    \"cols\": {cols},");
                lines.Add($"    \"rows\": {rows},");
                lines.Add($"    \"colNames\": {JsonArray(colNames)},");
                lines.Add($"    \"rowNames\": {JsonArray(rowNames)},");
                lines.Add($"    \"rowHeader\": {(rowHeader ? "true" : "false")},");
                lines.Add($"    \"cellButton\": {(cellButton ? "true" : "false")},");
                lines.Add($"    \"buttonEvent\": {Q(be)}");
                lines.Add("  }");
                break;
            case ProgramSettingKind.Rect:
                lines.Add("  \"defaultRaw\": \"\"");
                break;
            case ProgramSettingKind.File:
                lines.Add($"  \"defaultRaw\": {Q(def)},");
                lines.Add($"  \"filter\": {Q(string.IsNullOrWhiteSpace(filter) ? "*.json;*.txt" : filter)}");
                break;
            case ProgramSettingKind.Enum:
            case ProgramSettingKind.Radio:
            case ProgramSettingKind.Checks:
                var d = string.IsNullOrWhiteSpace(def)
                    ? (kind == ProgramSettingKind.Checks
                        ? string.Join(",", options.Take(Math.Min(2, options.Count)))
                        : options[0])
                    : def;
                lines.Add($"  \"defaultRaw\": {Q(d)},");
                lines.Add($"  \"enumOptions\": {JsonArray(options)}");
                break;
            default:
                lines.Add($"  \"defaultRaw\": {Q(ResolveDefault(kind, def, options))}");
                break;
        }

        lines.Add("}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSettingsValue(
        ProgramSettingKind kind, string key, string def, IReadOnlyList<string> options)
    {
        if (kind == ProgramSettingKind.Button)
            return "// 按钮无持久取值（仅事件队列）";
        if (kind == ProgramSettingKind.Table)
            return $"{Q(key)}: {{\"v\":1,\"cells\":[[\"\",\"\"],[\"\",\"\"]]}}";
        if (kind == ProgramSettingKind.Rect)
            return $"{Q(key)}: \"0.1,0.2,0.3,0.15\"  // x,y,w,h";

        var value = ResolveDefault(kind, def, options);
        if (kind == ProgramSettingKind.Bool)
        {
            var b = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value == "1"
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            return $"{Q(key)}: {(b ? "true" : "false")}";
        }

        return $"{Q(key)}: {Q(value)}";
    }

    private static string BuildApiHint(
        ProgramSettingKind kind, string key, string eventName, string buttonEvent)
    {
        var k = Q(key).Trim('"');
        return kind switch
        {
            ProgramSettingKind.Bool => $"Fly.GetBool(\"{k}\")",
            ProgramSettingKind.String or ProgramSettingKind.Enum or ProgramSettingKind.Radio
                or ProgramSettingKind.File or ProgramSettingKind.Folder
                => $"Fly.GetString(\"{k}\")",
            ProgramSettingKind.StringList or ProgramSettingKind.Checks
                => $"Fly.GetStringList(\"{k}\")",
            ProgramSettingKind.Int => $"Fly.GetInt(\"{k}\")",
            ProgramSettingKind.Long => $"Fly.GetLong(\"{k}\")",
            ProgramSettingKind.Double => $"Fly.GetDouble(\"{k}\")",
            ProgramSettingKind.Button =>
                $"Fly.TryConsumeSettingEvent(\"{(string.IsNullOrWhiteSpace(eventName) ? "Reset" : eventName)}\")",
            ProgramSettingKind.Table =>
                $"Fly.GetTable(\"{k}\")"
                + (string.IsNullOrWhiteSpace(buttonEvent) && string.IsNullOrWhiteSpace(eventName)
                    ? ""
                    : $"\n// 单元格按钮: TryConsumeSettingEvent(\"{(string.IsNullOrWhiteSpace(buttonEvent) ? eventName : buttonEvent)}\") → detail=\"row,col\""),
            ProgramSettingKind.Rect => $"Fly.GetRect(\"{k}\")",
            _ => $"Fly.GetString(\"{k}\")"
        };
    }

    private static string ResolveDefault(
        ProgramSettingKind kind, string def, IReadOnlyList<string> options)
    {
        if (!string.IsNullOrWhiteSpace(def))
            return def.Trim();
        return kind switch
        {
            ProgramSettingKind.Bool => "false",
            ProgramSettingKind.Int or ProgramSettingKind.Long => "0",
            ProgramSettingKind.Double => "0.0",
            ProgramSettingKind.StringList => "a,b",
            ProgramSettingKind.Enum or ProgramSettingKind.Radio => options.Count > 0 ? options[0] : "",
            ProgramSettingKind.Checks => options.Count > 0
                ? string.Join(",", options.Take(Math.Min(2, options.Count)))
                : "",
            ProgramSettingKind.File => "",
            ProgramSettingKind.Folder => "",
            _ => ""
        };
    }

    private static List<string> SplitList(string? raw)
        => (raw ?? "")
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static string Q(string s)
        => JsonSerializer.Serialize(s ?? "");

    private static string JsonArray(IReadOnlyList<string> items)
        => "[" + string.Join(", ", items.Select(Q)) + "]";
}
