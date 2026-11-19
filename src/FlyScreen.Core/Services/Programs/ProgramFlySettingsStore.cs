using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyScreen.Services.Programs;

/// <summary>
/// 独立 schema：<c>fly-settings.json</c>。
/// <c>settings.json</c> 仍只存运行时取值。源码注释 <c>@fly-settings</c> 已弃用。
/// </summary>
public static class ProgramFlySettingsStore
{
    public const string SchemaFileName = "fly-settings.json";

    public static readonly string EmptySchemaText =
        """
        {
          "version": 1,
          "fields": []
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string PathFor(ProgramCatalog catalog, UserProgram prog)
        => Path.Combine(catalog.GetFolder(prog), SchemaFileName);

    /// <summary>仅从 fly-settings.json 读取；文件缺失或无效时返回空列表。</summary>
    public static IReadOnlyList<ProgramSettingField> LoadSchema(ProgramCatalog catalog, UserProgram prog)
    {
        var path = PathFor(catalog, prog);
        if (!File.Exists(path)) return [];
        try
        {
            var text = File.ReadAllText(path);
            if (!IsValidSchemaText(text))
            {
                AppLog.Warn($"{SchemaFileName} 内容无效，已忽略", prog.Id, prog.Name);
                return [];
            }
            return LoadFromFile(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 {SchemaFileName} 失败: {ex.Message}", prog.Id, prog.Name);
            return [];
        }
    }

    /// <summary>合法 JSON 对象即可（允许空 fields）。</summary>
    public static bool IsValidSchemaText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>规范化可写回磁盘的 schema 文本；无效则返回空模板。</summary>
    public static string NormalizeSchemaText(string? text)
        => IsValidSchemaText(text) ? text!.Trim() + Environment.NewLine : EmptySchemaText;

    public static IReadOnlyList<ProgramSettingField> LoadFromFile(string path)
    {
        if (!File.Exists(path)) return [];
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<FlySettingsSchemaFile>(json, JsonOptions);
        if (file?.Fields == null) return [];
        return file.Fields.Select(ToField).Where(f => f != null).Cast<ProgramSettingField>().ToList();
    }

    public static void SaveSchema(ProgramCatalog catalog, UserProgram prog, IReadOnlyList<ProgramSettingField> fields)
    {
        var path = PathFor(catalog, prog);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new FlySettingsSchemaFile
        {
            Version = 1,
            Fields = fields.Select(FromField).ToList()
        };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(path, json);
        prog.UpdatedUtc = DateTime.UtcNow;
        catalog.SaveMeta(prog);
    }

    public static FlySettingsFieldDto FromField(ProgramSettingField f)
        => new()
        {
            Key = f.Key,
            Kind = f.Kind,
            Label = f.Label,
            DefaultRaw = f.DefaultRaw,
            EnumOptions = f.EnumOptions.ToList(),
            Filter = f.Filter,
            EventName = f.EventName,
            Table = f.Table == null
                ? null
                : new FlySettingsTableDto
                {
                    Cols = f.Table.Cols,
                    Rows = f.Table.Rows,
                    ColNames = f.Table.ColNames.ToList(),
                    RowNames = f.Table.RowNames.ToList(),
                    RowHeader = f.Table.RowHeader,
                    CellButton = f.Table.CellButton,
                    ButtonEvent = f.Table.ButtonEvent
                }
        };

    public static ProgramSettingField? ToField(FlySettingsFieldDto? d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Key) || string.IsNullOrWhiteSpace(d.Label))
            return null;
        ProgramTableSpec? table = null;
        if (d.Kind == ProgramSettingKind.Table)
        {
            var cols = Math.Clamp(d.Table?.Cols ?? 1, 1, 20);
            var rows = Math.Clamp(d.Table?.Rows ?? 1, 1, 50);
            var colNames = d.Table?.ColNames is { Count: > 0 } cn
                ? cn
                : Enumerable.Range(1, cols).Select(i => "C" + i).ToList();
            var rowNames = d.Table?.RowNames is { Count: > 0 } rn
                ? rn
                : Enumerable.Range(1, rows).Select(i => "R" + i).ToList();
            table = new ProgramTableSpec
            {
                Cols = cols,
                Rows = rows,
                ColNames = colNames,
                RowNames = rowNames,
                RowHeader = d.Table?.RowHeader ?? false,
                CellButton = d.Table?.CellButton ?? false,
                ButtonEvent = d.Table?.ButtonEvent ?? d.EventName ?? ""
            };
        }

        var defaultRaw = d.DefaultRaw ?? "";
        if (d.Kind == ProgramSettingKind.Table && string.IsNullOrWhiteSpace(defaultRaw) && table != null)
            defaultRaw = ProgramTableData.CreateEmpty(table.Rows, table.Cols).ToJson();

        return new ProgramSettingField
        {
            Key = d.Key.Trim(),
            Kind = d.Kind,
            Label = d.Label.Trim(),
            DefaultRaw = defaultRaw,
            EnumOptions = d.EnumOptions ?? [],
            Filter = d.Filter ?? "",
            EventName = string.IsNullOrWhiteSpace(d.EventName)
                ? (table?.ButtonEvent ?? "")
                : d.EventName.Trim(),
            Table = table
        };
    }
}

public sealed class FlySettingsSchemaFile
{
    public int Version { get; set; } = 1;
    public List<FlySettingsFieldDto> Fields { get; set; } = [];
}

public sealed class FlySettingsFieldDto
{
    public string Key { get; set; } = "";
    public ProgramSettingKind Kind { get; set; } = ProgramSettingKind.String;
    public string Label { get; set; } = "";
    public string DefaultRaw { get; set; } = "";
    public List<string> EnumOptions { get; set; } = [];
    public string Filter { get; set; } = "";
    public string EventName { get; set; } = "";
    public FlySettingsTableDto? Table { get; set; }
}

public sealed class FlySettingsTableDto
{
    public int Cols { get; set; } = 1;
    public int Rows { get; set; } = 1;
    public List<string> ColNames { get; set; } = [];
    public List<string> RowNames { get; set; } = [];
    public bool RowHeader { get; set; }
    public bool CellButton { get; set; }
    public string ButtonEvent { get; set; } = "";
}

/// <summary>可视化编辑器用的可变字段模型。</summary>
public sealed class FlySettingsFieldEdit : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;
    private string _key = "field1";
    private ProgramSettingKind _kind = ProgramSettingKind.String;
    private string _label = "字段";
    private string _defaultRaw = "";
    private string _enumOptionsText = "";
    private string _filter = "";
    private string _eventName = "";
    private int _tableCols = 2;
    private int _tableRows = 2;
    private string _colNamesText = "A,B";
    private string _rowNamesText = "R1,R2";
    private bool _rowHeader;
    private bool _cellButton;
    private string _buttonEvent = "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnChanged(nameof(IsSelected)); }
    }

    public string Key
    {
        get => _key;
        set { if (_key == value) return; _key = value; OnChanged(nameof(Key)); }
    }

    public ProgramSettingKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            OnChanged(nameof(Kind));
            OnChanged(nameof(KindText));
            OnChanged(nameof(EventDisplay));
        }
    }

    public string Label
    {
        get => _label;
        set { if (_label == value) return; _label = value; OnChanged(nameof(Label)); }
    }

    public string DefaultRaw
    {
        get => _defaultRaw;
        set { if (_defaultRaw == value) return; _defaultRaw = value; OnChanged(nameof(DefaultRaw)); }
    }

    public string EnumOptionsText
    {
        get => _enumOptionsText;
        set { if (_enumOptionsText == value) return; _enumOptionsText = value; OnChanged(nameof(EnumOptionsText)); }
    }

    public string Filter
    {
        get => _filter;
        set { if (_filter == value) return; _filter = value; OnChanged(nameof(Filter)); }
    }

    public string EventName
    {
        get => _eventName;
        set
        {
            if (_eventName == value) return;
            _eventName = value;
            OnChanged(nameof(EventName));
            OnChanged(nameof(EventDisplay));
        }
    }

    public int TableCols
    {
        get => _tableCols;
        set { if (_tableCols == value) return; _tableCols = value; OnChanged(nameof(TableCols)); }
    }

    public int TableRows
    {
        get => _tableRows;
        set { if (_tableRows == value) return; _tableRows = value; OnChanged(nameof(TableRows)); }
    }

    public string ColNamesText
    {
        get => _colNamesText;
        set { if (_colNamesText == value) return; _colNamesText = value; OnChanged(nameof(ColNamesText)); }
    }

    public string RowNamesText
    {
        get => _rowNamesText;
        set { if (_rowNamesText == value) return; _rowNamesText = value; OnChanged(nameof(RowNamesText)); }
    }

    public bool RowHeader
    {
        get => _rowHeader;
        set { if (_rowHeader == value) return; _rowHeader = value; OnChanged(nameof(RowHeader)); }
    }

    public bool CellButton
    {
        get => _cellButton;
        set { if (_cellButton == value) return; _cellButton = value; OnChanged(nameof(CellButton)); }
    }

    public string ButtonEvent
    {
        get => _buttonEvent;
        set
        {
            if (_buttonEvent == value) return;
            _buttonEvent = value;
            OnChanged(nameof(ButtonEvent));
            OnChanged(nameof(EventDisplay));
        }
    }

    public string KindText => Kind.ToString();

    /// <summary>表格「事件」列：按钮用 EventName，表格用 ButtonEvent。</summary>
    public string EventDisplay => Kind switch
    {
        ProgramSettingKind.Button => EventName ?? "",
        ProgramSettingKind.Table => string.IsNullOrWhiteSpace(ButtonEvent) ? (EventName ?? "") : ButtonEvent,
        _ => EventName ?? ""
    };

    public static FlySettingsFieldEdit FromField(ProgramSettingField f)
    {
        return new FlySettingsFieldEdit
        {
            Key = f.Key,
            Kind = f.Kind,
            Label = f.Label,
            DefaultRaw = f.Kind == ProgramSettingKind.Table ? "" : f.DefaultRaw,
            EnumOptionsText = string.Join(",", f.EnumOptions),
            Filter = f.Filter,
            EventName = f.EventName,
            TableCols = f.Table?.Cols ?? 2,
            TableRows = f.Table?.Rows ?? 2,
            ColNamesText = f.Table != null ? string.Join(",", f.Table.ColNames) : "A,B",
            RowNamesText = f.Table != null ? string.Join(",", f.Table.RowNames) : "R1,R2",
            RowHeader = f.Table?.RowHeader ?? false,
            CellButton = f.Table?.CellButton ?? false,
            ButtonEvent = f.Table?.ButtonEvent ?? f.EventName
        };
    }

    public FlySettingsFieldEdit Clone()
    {
        return new FlySettingsFieldEdit
        {
            Key = Key,
            Kind = Kind,
            Label = Label,
            DefaultRaw = DefaultRaw,
            EnumOptionsText = EnumOptionsText,
            Filter = Filter,
            EventName = EventName,
            TableCols = TableCols,
            TableRows = TableRows,
            ColNamesText = ColNamesText,
            RowNamesText = RowNamesText,
            RowHeader = RowHeader,
            CellButton = CellButton,
            ButtonEvent = ButtonEvent
        };
    }

    public void CopyFrom(FlySettingsFieldEdit o)
    {
        Key = o.Key;
        Kind = o.Kind;
        Label = o.Label;
        DefaultRaw = o.DefaultRaw;
        EnumOptionsText = o.EnumOptionsText;
        Filter = o.Filter;
        EventName = o.EventName;
        TableCols = o.TableCols;
        TableRows = o.TableRows;
        ColNamesText = o.ColNamesText;
        RowNamesText = o.RowNamesText;
        RowHeader = o.RowHeader;
        CellButton = o.CellButton;
        ButtonEvent = o.ButtonEvent;
    }

    public ProgramSettingField ToField()
    {
        var dto = new FlySettingsFieldDto
        {
            Key = Key.Trim(),
            Kind = Kind,
            Label = Label.Trim(),
            DefaultRaw = DefaultRaw ?? "",
            EnumOptions = (EnumOptionsText ?? "")
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            Filter = Filter ?? "",
            EventName = EventName ?? "",
            Table = Kind == ProgramSettingKind.Table
                ? new FlySettingsTableDto
                {
                    Cols = Math.Clamp(TableCols, 1, 20),
                    Rows = Math.Clamp(TableRows, 1, 50),
                    ColNames = (ColNamesText ?? "")
                        .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList(),
                    RowNames = (RowNamesText ?? "")
                        .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList(),
                    RowHeader = RowHeader,
                    CellButton = CellButton,
                    ButtonEvent = string.IsNullOrWhiteSpace(ButtonEvent) ? (EventName ?? "") : ButtonEvent
                }
                : null
        };
        return ProgramFlySettingsStore.ToField(dto)
               ?? new ProgramSettingField { Key = "field", Kind = ProgramSettingKind.String, Label = "字段" };
    }

    public string DisplayLine => $"{Kind}  {Key}  —  {Label}";

    public override string ToString() => DisplayLine;

    private void OnChanged(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
