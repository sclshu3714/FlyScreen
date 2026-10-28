namespace FlyScreen.Services.Programs;

/// <summary>竖屏/横屏识别区与操作区设置键约定，及旧键迁移。</summary>
public static class ProgramRegionKeys
{
    public const string OcrPortrait = "ocrRegionPortrait";
    public const string OcrLandscape = "ocrRegionLandscape";
    public const string ActionPortrait = "actionRegionPortrait";
    public const string ActionLandscape = "actionRegionLandscape";

    /// <summary>旧版单键（视为竖屏）。</summary>
    public const string LegacyOcr = "ocrRegion";

    /// <summary>旧版单键（视为竖屏）。</summary>
    public const string LegacyAction = "actionRegion";

    public static bool IsLandscape(int screenW, int screenH) => screenW > screenH;

    public static string OcrKey(bool landscape) => landscape ? OcrLandscape : OcrPortrait;

    public static string ActionKey(bool landscape) => landscape ? ActionLandscape : ActionPortrait;

    public static bool IsActionKey(string key)
        => key.Contains("action", StringComparison.OrdinalIgnoreCase);

    public static bool IsOcrKey(string key)
        => key.Contains("ocr", StringComparison.OrdinalIgnoreCase);

    public static bool IsLandscapeKey(string key)
        => key.Contains("Landscape", StringComparison.OrdinalIgnoreCase);

    public static NormRect DefaultRect(string settingKey)
        => IsActionKey(settingKey)
            ? new NormRect(0.25, 0.20, 0.45, 0.55)
            : new NormRect(0.28, 0.02, 0.70, 0.30);

    public static string Caption(string settingKey)
    {
        var orient = IsLandscapeKey(settingKey) ? "横屏" : "竖屏";
        var kind = IsActionKey(settingKey) ? "操作区" : "识别区";
        return orient + kind;
    }

    /// <summary>旧 ocrRegion/actionRegion → 竖屏键（仅当竖屏键为空时）。</summary>
    public static void MigrateLegacy(IDictionary<string, string> map)
    {
        CopyIfEmpty(map, OcrPortrait, LegacyOcr);
        CopyIfEmpty(map, ActionPortrait, LegacyAction);
    }

    public static bool HasConfiguredRect(IDictionary<string, string>? map, string key)
    {
        if (map == null) return false;
        if (!map.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        var r = NormRect.TryParse(raw);
        return r != null && !r.Value.IsEmpty;
    }

    /// <summary>
    /// 按当前朝向选键：只用该朝向专用键；竖屏未设时可回退旧单键。横/竖互不借用对方标定。
    /// </summary>
    public static string ResolveKey(IDictionary<string, string>? map, bool landscape, bool action)
    {
        var primary = action ? ActionKey(landscape) : OcrKey(landscape);
        if (HasConfiguredRect(map, primary)) return primary;

        if (!landscape)
        {
            var legacy = action ? LegacyAction : LegacyOcr;
            if (HasConfiguredRect(map, legacy)) return legacy;
        }

        return primary;
    }

    /// <summary>表格展示用（三位小数）；磁盘/计算仍用 <see cref="NormRect.ToString"/> 完整精度。</summary>
    public static string FormatCell(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var r = NormRect.TryParse(raw);
        return r == null || r.Value.IsEmpty ? "" : r.Value.ToDisplayString(3);
    }

    private static void CopyIfEmpty(IDictionary<string, string> map, string dest, string src)
    {
        if (HasConfiguredRect(map, dest)) return;
        if (!map.TryGetValue(src, out var v) || string.IsNullOrWhiteSpace(v)) return;
        map[dest] = v;
    }
}
