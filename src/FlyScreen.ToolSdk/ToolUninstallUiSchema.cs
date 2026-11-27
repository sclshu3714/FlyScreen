using System.Text.Json.Serialization;

namespace FlyScreen.ToolSdk;

/// <summary>工具卸载决策 UI 声明（与安装目录 <c>uninstall-ui.json</c> 同形）。</summary>
public sealed class ToolUninstallUiSchema
{
    public const string FileName = "uninstall-ui.json";
    public const string RequestKind = "uninstall-decisions";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("fields")]
    public List<ToolUninstallUiField> Fields { get; set; } = [];

    /// <summary>静默卸载或未填字段时使用的默认答案（field id → value）。</summary>
    [JsonPropertyName("defaults")]
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ToolUninstallUiField
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary><c>choice</c> | <c>password</c> | <c>folder</c> | <c>confirm</c></summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "choice";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("options")]
    public List<ToolUninstallUiOption>? Options { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>简单条件：<c>fieldId=value</c>，满足时显示。</summary>
    [JsonPropertyName("visibleWhen")]
    public string? VisibleWhen { get; set; }
}

public sealed class ToolUninstallUiOption
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

/// <summary>通用卸载决策结果（宿主对话框 → policy）。</summary>
public sealed class ToolUninstallAnswers
{
    public bool Cancelled { get; init; }

    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static ToolUninstallAnswers Cancel() => new() { Cancelled = true };

    public static ToolUninstallAnswers From(IReadOnlyDictionary<string, string> values)
        => new()
        {
            Cancelled = false,
            Values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
        };
}

/// <summary>产品卸载器 / CLI 批处理文件格式。</summary>
public sealed class ToolUninstallBatchFile
{
    [JsonPropertyName("tools")]
    public List<ToolUninstallBatchItem> Tools { get; set; } = [];
}

public sealed class ToolUninstallBatchItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("answers")]
    public Dictionary<string, string> Answers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
