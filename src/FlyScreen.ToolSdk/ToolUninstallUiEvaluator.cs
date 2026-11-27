namespace FlyScreen.ToolSdk;

/// <summary>卸载决策表单：可见性与必填校验（宿主/卸载器共用）。</summary>
public static class ToolUninstallUiEvaluator
{
    public static bool IsVisible(ToolUninstallUiField field, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(field.VisibleWhen))
            return true;

        var raw = field.VisibleWhen.Trim();
        var eq = raw.IndexOf('=');
        if (eq <= 0)
            return true;

        var key = raw[..eq].Trim();
        var expect = raw[(eq + 1)..].Trim();
        if (!values.TryGetValue(key, out var actual))
            return false;
        return string.Equals(actual, expect, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ValidateRequired(ToolUninstallUiSchema schema, IReadOnlyDictionary<string, string> values)
    {
        foreach (var field in schema.Fields)
        {
            if (!field.Required || !IsVisible(field, values))
                continue;
            if (!values.TryGetValue(field.Id, out var v) || string.IsNullOrWhiteSpace(v))
                return string.IsNullOrWhiteSpace(field.Label) ? field.Id : field.Label;
        }
        return null;
    }

    public static bool TryParseVisibleWhen(string? visibleWhen, out string fieldId, out string expectedValue)
    {
        fieldId = "";
        expectedValue = "";
        if (string.IsNullOrWhiteSpace(visibleWhen))
            return false;
        var eq = visibleWhen.IndexOf('=');
        if (eq <= 0) return false;
        fieldId = visibleWhen[..eq].Trim();
        expectedValue = visibleWhen[(eq + 1)..].Trim();
        return fieldId.Length > 0;
    }
}
