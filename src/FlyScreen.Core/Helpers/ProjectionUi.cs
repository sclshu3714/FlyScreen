using FlyScreen.Helpers.Localization;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Helpers;

/// <summary>投影入口所处会话阶段（跨端标题栏 / 托盘 / 触控板共用）。</summary>
public enum ProjectionUiPhase
{
    /// <summary>可发起投影。</summary>
    Idle,
    /// <summary>本机正在向对端投影。</summary>
    Hosting,
    /// <summary>本机正在接收对端投影。</summary>
    Receiving
}

/// <summary>投影入口一帧外观：图标 + 文案 + 提示。</summary>
public readonly record struct ProjectionUiChrome(
    /// <summary>Segoe MDL2 Assets 字形（Win / Maui-Win 标题栏与托盘）。</summary>
    string Mdl2Glyph,
    /// <summary>跨端 Unicode 图标（Avalonia / 手机）。</summary>
    string UnicodeGlyph,
    string Label,
    string Tooltip,
    /// <summary>是否用强调色（投影中 / 接收中）。</summary>
    bool Emphasized,
    double SuggestedWidth);

/// <summary>
/// 投影 UI 状态机（标题栏与托盘必须同文案、同图标语义）：
/// <list type="bullet">
/// <item>空闲默认：投屏图标 +「投影」</item>
/// <item>空闲悬停：连接图标 +「投影」（提示打开设置）</item>
/// <item>投影中：投屏图标 +「投影中」</item>
/// <item>投影中悬停 / 托盘：停止图标 +「停止投影」</item>
/// <item>接收中：接收图标 +「接收中」</item>
/// </list>
/// </summary>
public static class ProjectionUi
{
    // Segoe MDL2：Miracast(投屏) / Connect(悬停) / Stop / Streaming
    private const string Mdl2Cast = "\uE8B9";
    private const string Mdl2Connect = "\uE8F2";
    private const string Mdl2Stop = "\uE71A";
    private const string Mdl2Receive = "\uE8EA";

    // Unicode 兜底（无 MDL2 的端）：避免 ⛶ 等在中文字体里变成「虚线方框」
    private const string UniCast = "▣";
    private const string UniConnect = "◈";
    private const string UniHosting = "●";
    private const string UniStop = "■";
    private const string UniReceive = "↓";

    public static ProjectionUiPhase Resolve(bool isActive, ScreenExtendRole role)
    {
        if (!isActive || role == ScreenExtendRole.None)
            return ProjectionUiPhase.Idle;
        return role == ScreenExtendRole.Host
            ? ProjectionUiPhase.Hosting
            : ProjectionUiPhase.Receiving;
    }

    public static ProjectionUiPhase Resolve(ScreenExtendService session)
        => Resolve(session.IsActive, session.Role);

    /// <summary>标题栏 / 手机顶栏 / 托盘：同一套 Label + 图标。</summary>
    public static ProjectionUiChrome Title(ProjectionUiPhase phase, bool hovered = false)
        => Chrome(phase, hovered, forTray: false);

    /// <summary>托盘菜单：与标题栏文案、图标完全一致；Hosting 默认即「停止投影」。</summary>
    public static ProjectionUiChrome Tray(ProjectionUiPhase phase, bool hovered = false)
        => Chrome(phase, hovered, forTray: true);

    private static ProjectionUiChrome Chrome(ProjectionUiPhase phase, bool hovered, bool forTray)
        => (phase, hovered, forTray) switch
        {
            // 空闲：▣/投屏 + 投影；悬停换连接图标，文案仍为「投影」
            (ProjectionUiPhase.Idle, false, _) => new(
                Mdl2Cast, UniCast,
                Loc.T("ProjUi.Idle.Label"),
                Loc.T("ProjUi.Idle.Tip"),
                false, 78),
            (ProjectionUiPhase.Idle, true, _) => new(
                Mdl2Connect, UniConnect,
                Loc.T("ProjUi.Idle.Label"),
                Loc.T("ProjUi.Idle.HoverTip"),
                false, 78),

            // 投影中：标题默认「投影中」；托盘默认直接「停止投影」（菜单偏动作）
            (ProjectionUiPhase.Hosting, false, false) => new(
                Mdl2Cast, UniHosting,
                Loc.T("ProjUi.Host.Label"),
                Loc.T("ProjUi.Host.Tip"),
                true, 88),
            (ProjectionUiPhase.Hosting, false, true) => new(
                Mdl2Stop, UniStop,
                Loc.T("ProjUi.Host.HoverLabel"),
                Loc.T("ProjUi.Host.HoverTip"),
                true, 0),
            (ProjectionUiPhase.Hosting, true, _) => new(
                Mdl2Stop, UniStop,
                Loc.T("ProjUi.Host.HoverLabel"),
                Loc.T("ProjUi.Host.HoverTip"),
                true, 100),

            (ProjectionUiPhase.Receiving, _, _) => new(
                Mdl2Receive, UniReceive,
                Loc.T("ProjUi.Sink.Label"),
                Loc.T("ProjUi.Sink.Tip"),
                true, 88),

            _ => Chrome(ProjectionUiPhase.Idle, false, forTray)
        };

    /// <summary>单行展示「图标 + 文案」（无 MDL2 字体的端用 Unicode）。</summary>
    public static string FormatLine(ProjectionUiChrome chrome, bool preferUnicode = true)
        => $"{(preferUnicode ? chrome.UnicodeGlyph : chrome.Mdl2Glyph)}  {chrome.Label}";
}
