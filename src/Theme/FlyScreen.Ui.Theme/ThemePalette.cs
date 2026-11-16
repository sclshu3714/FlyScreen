namespace FlyScreen.Ui.Theme;

/// <summary>浅/深色主题色值（hex）。各端 ThemeService 据此覆写同名资源键。</summary>
public sealed record ThemeColors(
    string Blue,
    string BlueSoft,
    string BlueHover,
    string BluePress,
    string Line,
    string Muted,
    string Text,
    string Success,
    string SuccessSoft,
    string Warn,
    string WarnSoft,
    string WinBg,
    string CardBg,
    string PaneBg,
    string SubtleBg,
    string ControlBg,
    string ControlBorder,
    string ControlHover,
    string Placeholder,
    string MatrixBg,
    string ListBg,
    string ToolCardNotInstalledBg,
    string ToolCardInstalledBg,
    string ToolCardRunningBg,
    string ToolCardInstallingBg,
    string ToolCardProgress,
    string ScrollTrack,
    string ScrollThumb,
    string ScrollThumbHover,
    string ScrollThumbDrag,
    string Primary);

/// <summary>
/// 可发现主题描述。扩展主题时 <see cref="ThemePalette.Register"/> 注册即可，
/// 各端下拉框通过 <see cref="ThemePalette.All"/> 动态枚举，无需改 UI 分支。
/// </summary>
public sealed record ThemeInfo(
    string Id,
    string DisplayLocKey,
    ThemeColors Colors,
    bool IsDark,
    ThemeColors? MauiColors = null)
{
    public ThemeColors ResolveColors(bool maui = false)
        => maui && MauiColors is not null ? MauiColors : Colors;
}

/// <summary>统一主题色板：WPF / MAUI / Avalonia 共用同一组 key；主题列表可注册扩展。</summary>
public static class ThemePalette
{
    public const string LightName = "Light";
    public const string DarkName = "Dark";

    public static ThemeColors Light { get; } = new(
        Blue: "#2563EB",
        BlueSoft: "#DBEAFE",
        BlueHover: "#EFF6FF",
        BluePress: "#1D4ED8",
        Line: "#E5E7EB",
        Muted: "#6B7280",
        Text: "#111827",
        Success: "#059669",
        SuccessSoft: "#ECFDF5",
        Warn: "#D97706",
        WarnSoft: "#FEF3C7",
        WinBg: "#F7F8FA",
        CardBg: "#FFFFFF",
        PaneBg: "#FFFFFF",
        SubtleBg: "#F3F4F6",
        ControlBg: "#FFFFFF",
        ControlBorder: "#D1D5DB",
        ControlHover: "#F9FAFB",
        Placeholder: "#9CA3AF",
        MatrixBg: "#F8FAFC",
        ListBg: "#F9FAFB",
        ToolCardNotInstalledBg: "#F3F4F6",
        ToolCardInstalledBg: "#ECFDF5",
        ToolCardRunningBg: "#DBEAFE",
        ToolCardInstallingBg: "#EFF6FF",
        ToolCardProgress: "#93C5FD",
        ScrollTrack: "#00000000",
        ScrollThumb: "#CBD5E1",
        ScrollThumbHover: "#94A3B8",
        ScrollThumbDrag: "#64748B",
        Primary: "#2563EB");

    public static ThemeColors Dark { get; } = new(
        Blue: "#3B82F6",
        BlueSoft: "#1E3A5F",
        BlueHover: "#243447",
        BluePress: "#1D4ED8",
        Line: "#334155",
        Muted: "#94A3B8",
        Text: "#E2E8F0",
        Success: "#34D399",
        SuccessSoft: "#0F3D2E",
        Warn: "#FBBF24",
        WarnSoft: "#422006",
        WinBg: "#0F172A",
        CardBg: "#1E293B",
        PaneBg: "#111827",
        SubtleBg: "#1E293B",
        ControlBg: "#0F172A",
        ControlBorder: "#475569",
        ControlHover: "#334155",
        Placeholder: "#64748B",
        MatrixBg: "#0B1220",
        ListBg: "#0B1220",
        ToolCardNotInstalledBg: "#1E293B",
        ToolCardInstalledBg: "#0F3D2E",
        ToolCardRunningBg: "#1E3A5F",
        ToolCardInstallingBg: "#243447",
        ToolCardProgress: "#3B82F6",
        ScrollTrack: "#00000000",
        ScrollThumb: "#64748B",
        ScrollThumbHover: "#94A3B8",
        ScrollThumbDrag: "#CBD5E1",
        Primary: "#3B82F6");

    /// <summary>MAUI 暗色 ControlBg 略浅于卡片，保持历史观感。</summary>
    public static ThemeColors DarkMaui { get; } = Dark with { ControlBg = "#0B1220" };

    private static readonly object Gate = new();
    private static readonly List<ThemeInfo> Themes =
    [
        new(LightName, "Sys.StyleLight", Light, IsDark: false),
        new(DarkName, "Sys.StyleDark", Dark, IsDark: true, MauiColors: DarkMaui)
    ];

    /// <summary>已注册主题（只读快照，按注册顺序）。</summary>
    public static IReadOnlyList<ThemeInfo> All
    {
        get
        {
            lock (Gate)
                return Themes.ToArray();
        }
    }

    /// <summary>
    /// 注册或覆盖同 Id 主题。扩展包在启动时调用即可出现在系统设置下拉框中。
    /// </summary>
    public static void Register(ThemeInfo theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (string.IsNullOrWhiteSpace(theme.Id))
            throw new ArgumentException("主题 Id 不能为空", nameof(theme));

        lock (Gate)
        {
            var idx = Themes.FindIndex(t =>
                string.Equals(t.Id, theme.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                Themes[idx] = theme;
            else
                Themes.Add(theme);
        }
    }

    public static ThemeInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (Gate)
        {
            return Themes.FirstOrDefault(t =>
                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>解析主题；未知 Id 回退到淡色。</summary>
    public static ThemeInfo Resolve(string? id)
        => Find(id) ?? Themes[0];

    public static bool IsDark(string? theme)
        => Resolve(theme).IsDark;

    /// <summary>按 Id 取色板；未知回退淡色。</summary>
    public static ThemeColors Get(string? id, bool maui = false)
        => Resolve(id).ResolveColors(maui);

    /// <summary>兼容旧调用：按明暗取内置色板。</summary>
    public static ThemeColors Get(bool dark, bool maui = false)
        => Get(dark ? DarkName : LightName, maui);

    /// <summary>资源键 → hex，供各端 ThemeService 批量覆写。</summary>
    public static IReadOnlyDictionary<string, string> ToResourceMap(ThemeColors c) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Blue"] = c.Blue,
        ["BlueSoft"] = c.BlueSoft,
        ["BlueHover"] = c.BlueHover,
        ["BluePress"] = c.BluePress,
        ["Line"] = c.Line,
        ["Muted"] = c.Muted,
        ["Text"] = c.Text,
        ["Success"] = c.Success,
        ["SuccessSoft"] = c.SuccessSoft,
        ["Warn"] = c.Warn,
        ["WarnSoft"] = c.WarnSoft,
        ["WinBg"] = c.WinBg,
        ["CardBg"] = c.CardBg,
        ["PaneBg"] = c.PaneBg,
        ["SubtleBg"] = c.SubtleBg,
        ["ControlBg"] = c.ControlBg,
        ["ControlBorder"] = c.ControlBorder,
        ["ControlHover"] = c.ControlHover,
        ["Placeholder"] = c.Placeholder,
        ["MatrixBg"] = c.MatrixBg,
        ["ListBg"] = c.ListBg,
        ["ToolCardNotInstalledBg"] = c.ToolCardNotInstalledBg,
        ["ToolCardInstalledBg"] = c.ToolCardInstalledBg,
        ["ToolCardRunningBg"] = c.ToolCardRunningBg,
        ["ToolCardInstallingBg"] = c.ToolCardInstallingBg,
        ["ToolCardProgress"] = c.ToolCardProgress,
        ["ScrollTrack"] = c.ScrollTrack,
        ["ScrollThumb"] = c.ScrollThumb,
        ["ScrollThumbHover"] = c.ScrollThumbHover,
        ["ScrollThumbDrag"] = c.ScrollThumbDrag,
        ["Primary"] = c.Primary,
        ["White"] = "#FFFFFF"
    };
}
