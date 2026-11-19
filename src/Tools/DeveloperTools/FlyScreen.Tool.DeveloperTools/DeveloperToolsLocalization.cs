using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.DeveloperTools;

public sealed class DeveloperToolsLocalization : IToolLocalization
{
    public static DeveloperToolsLocalization Instance { get; } = new();

    static DeveloperToolsLocalization() => EnsureRegistered();

    public string SourceId => "developer-tools";

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; } = Build();

    public static void EnsureRegistered() => Loc.RegisterPlugin(Instance);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Build()
        => new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = Zh(),
            ["en-US"] = En()
        };

    private static Dictionary<string, string> Zh() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.DeveloperTools.Name"] = "开发者工具",
        ["Tool.DeveloperTools.Summary"] = "创建独立插件工具的 VS / VS Code 脚手架与模板（工作区 / 弹窗 / 后台）。",
        ["Tool.DeveloperTools.Hint"] = "填写插件信息后点「生成」。成功后可用 Visual Studio 或 VS Code 打开工程。",
        ["Tool.DeveloperTools.DisplayNameZh"] = "中文名称（显示名）",
        ["Tool.DeveloperTools.ProjectNameEn"] = "英文名称（项目名）",
        ["Tool.DeveloperTools.SummaryLabel"] = "功能说明",
        ["Tool.DeveloperTools.Platforms"] = "支持端",
        ["Tool.DeveloperTools.Desktop"] = "电脑",
        ["Tool.DeveloperTools.Phone"] = "手机",
        ["Tool.DeveloperTools.UiMode"] = "界面形态",
        ["Tool.DeveloperTools.UiWorkspace"] = "嵌入主界面工作区",
        ["Tool.DeveloperTools.UiDialog"] = "启动弹窗（无嵌入）",
        ["Tool.DeveloperTools.UiBackground"] = "纯后台（无界面）",
        ["Tool.DeveloperTools.OutputRoot"] = "项目生成位置（默认 %AppData%/FlyScreen/Code）",
        ["Tool.DeveloperTools.Browse"] = "浏览…",
        ["Tool.DeveloperTools.RegisterRepo"] = "写入源码仓库注册点（仅输出在 src/Tools 时有效）",
        ["Tool.DeveloperTools.OpenHint"] = "生成后请打开 *.sln（不要单独打开某个 .csproj）",
        ["Tool.DeveloperTools.Generate"] = "生成",
        ["Tool.DeveloperTools.OpenVs"] = "用 Visual Studio 打开",
        ["Tool.DeveloperTools.OpenCode"] = "用 VS Code 打开",
        ["Tool.DeveloperTools.OpenFolder"] = "打开文件夹",
        ["Tool.DeveloperTools.Generating"] = "正在生成…",
        ["Tool.DeveloperTools.Done"] = "生成成功",
        ["Tool.DeveloperTools.Failed"] = "生成失败"
    };

    private static Dictionary<string, string> En() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.DeveloperTools.Name"] = "Developer Tools",
        ["Tool.DeveloperTools.Summary"] = "Scaffold independent plugin tools for Visual Studio / VS Code (workspace / dialog / background).",
        ["Tool.DeveloperTools.Hint"] = "Fill in the plugin info and click Generate. Then open the project in Visual Studio or VS Code.",
        ["Tool.DeveloperTools.DisplayNameZh"] = "Display name (Chinese)",
        ["Tool.DeveloperTools.ProjectNameEn"] = "Project name (English)",
        ["Tool.DeveloperTools.SummaryLabel"] = "Description",
        ["Tool.DeveloperTools.Platforms"] = "Targets",
        ["Tool.DeveloperTools.Desktop"] = "Desktop",
        ["Tool.DeveloperTools.Phone"] = "Phone",
        ["Tool.DeveloperTools.UiMode"] = "UI mode",
        ["Tool.DeveloperTools.UiWorkspace"] = "Embed workspace",
        ["Tool.DeveloperTools.UiDialog"] = "Startup dialog (no embed)",
        ["Tool.DeveloperTools.UiBackground"] = "Background only (no UI)",
        ["Tool.DeveloperTools.OutputRoot"] = "Output folder (default %AppData%/FlyScreen/Code)",
        ["Tool.DeveloperTools.Browse"] = "Browse…",
        ["Tool.DeveloperTools.RegisterRepo"] = "Register into source repo (only if output is under src/Tools)",
        ["Tool.DeveloperTools.OpenHint"] = "Open the generated *.sln (do not open a single .csproj)",
        ["Tool.DeveloperTools.Generate"] = "Generate",
        ["Tool.DeveloperTools.OpenVs"] = "Open in Visual Studio",
        ["Tool.DeveloperTools.OpenCode"] = "Open in VS Code",
        ["Tool.DeveloperTools.OpenFolder"] = "Open folder",
        ["Tool.DeveloperTools.Generating"] = "Generating…",
        ["Tool.DeveloperTools.Done"] = "Generated",
        ["Tool.DeveloperTools.Failed"] = "Failed"
    };
}
