using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy;

/// <summary>端口代理工具文案（独立于主程序 Loc 表）。</summary>
public sealed class PortProxyLocalization : IToolLocalization
{
    public static PortProxyLocalization Instance { get; } = new();

    static PortProxyLocalization() => EnsureRegistered();

    public string SourceId => "port-proxy";

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
        ["Tool.PortProxy.Name"] = "端口代理",
        ["Tool.PortProxy.Summary"] = "管理本机 netsh interface portproxy（v4tov4 / v4tov6 / v6tov4 / v6tov6）：查看、添加、修改、删除、重置与导出脚本。",
        ["Tool.PortProxy.Hint"] = "基于 Windows netsh interface portproxy。添加/修改/删除/重置通常需要管理员权限；未提升时会弹出 UAC。",
        ["Tool.PortProxy.AdminOk"] = "当前进程已具备管理员权限。",
        ["Tool.PortProxy.AdminNeed"] = "当前未以管理员运行；变更规则时将请求 UAC 提升。",
        ["Tool.PortProxy.Refresh"] = "刷新",
        ["Tool.PortProxy.Add"] = "添加",
        ["Tool.PortProxy.Edit"] = "修改",
        ["Tool.PortProxy.Delete"] = "删除",
        ["Tool.PortProxy.Reset"] = "重置全部",
        ["Tool.PortProxy.Dump"] = "导出脚本",
        ["Tool.PortProxy.CopyDump"] = "复制脚本",
        ["Tool.PortProxy.TabAll"] = "全部",
        ["Tool.PortProxy.ColKind"] = "类型",
        ["Tool.PortProxy.ColListenAddress"] = "侦听地址",
        ["Tool.PortProxy.ColListenPort"] = "侦听端口",
        ["Tool.PortProxy.ColConnectAddress"] = "连接地址",
        ["Tool.PortProxy.ColConnectPort"] = "连接端口",
        ["Tool.PortProxy.ColProtocol"] = "协议",
        ["Tool.PortProxy.ColActions"] = "操作",
        ["Tool.PortProxy.Empty"] = "暂无规则",
        ["Tool.PortProxy.SelectFirst"] = "请先勾选要删除的规则。",
        ["Tool.PortProxy.SelectAll"] = "全选",
        ["Tool.PortProxy.Filter"] = "筛选",
        ["Tool.PortProxy.ConfirmDelete"] = "确定删除所选端口代理规则？",
        ["Tool.PortProxy.ConfirmDeleteMany"] = "共 {0} 条规则",
        ["Tool.PortProxy.ConfirmReset"] = "确定重置全部 portproxy 规则？此操作不可撤销。",
        ["Tool.PortProxy.DialogAdd"] = "添加端口代理",
        ["Tool.PortProxy.DialogEdit"] = "修改端口代理",
        ["Tool.PortProxy.DialogDump"] = "portproxy dump",
        ["Tool.PortProxy.Kind"] = "映射类型",
        ["Tool.PortProxy.ListenAddress"] = "侦听地址 (listenaddress)",
        ["Tool.PortProxy.ListenPort"] = "侦听端口 (listenport)",
        ["Tool.PortProxy.ConnectAddress"] = "连接地址 (connectaddress)",
        ["Tool.PortProxy.ConnectPort"] = "连接端口 (connectport)",
        ["Tool.PortProxy.Protocol"] = "协议 (protocol)",
        ["Tool.PortProxy.Ok"] = "确定",
        ["Tool.PortProxy.Cancel"] = "取消",
        ["Tool.PortProxy.Close"] = "关闭",
        ["Tool.PortProxy.ValidationPort"] = "请填写有效的侦听端口。",
        ["Tool.PortProxy.ValidationConnect"] = "请填写连接地址。",
        ["Tool.PortProxy.Copied"] = "已复制到剪贴板",
        ["Tool.PortProxy.NotReady"] = "端口代理工具未安装或未加载"
    };

    private static Dictionary<string, string> En() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.PortProxy.Name"] = "Port Proxy",
        ["Tool.PortProxy.Summary"] = "Manage local netsh interface portproxy (v4tov4 / v4tov6 / v6tov4 / v6tov6): list, add, set, delete, reset, and dump.",
        ["Tool.PortProxy.Hint"] = "Wraps Windows netsh interface portproxy. Add/set/delete/reset usually require elevation; UAC will prompt when needed.",
        ["Tool.PortProxy.AdminOk"] = "This process is running elevated.",
        ["Tool.PortProxy.AdminNeed"] = "Not elevated; mutations will request UAC.",
        ["Tool.PortProxy.Refresh"] = "Refresh",
        ["Tool.PortProxy.Add"] = "Add",
        ["Tool.PortProxy.Edit"] = "Edit",
        ["Tool.PortProxy.Delete"] = "Delete",
        ["Tool.PortProxy.Reset"] = "Reset all",
        ["Tool.PortProxy.Dump"] = "Dump script",
        ["Tool.PortProxy.CopyDump"] = "Copy script",
        ["Tool.PortProxy.TabAll"] = "All",
        ["Tool.PortProxy.ColKind"] = "Kind",
        ["Tool.PortProxy.ColListenAddress"] = "Listen address",
        ["Tool.PortProxy.ColListenPort"] = "Listen port",
        ["Tool.PortProxy.ColConnectAddress"] = "Connect address",
        ["Tool.PortProxy.ColConnectPort"] = "Connect port",
        ["Tool.PortProxy.ColProtocol"] = "Protocol",
        ["Tool.PortProxy.ColActions"] = "Actions",
        ["Tool.PortProxy.Empty"] = "No rules",
        ["Tool.PortProxy.SelectFirst"] = "Select rules to delete first.",
        ["Tool.PortProxy.SelectAll"] = "Select all",
        ["Tool.PortProxy.Filter"] = "Filter",
        ["Tool.PortProxy.ConfirmDelete"] = "Delete the selected port proxy rule(s)?",
        ["Tool.PortProxy.ConfirmDeleteMany"] = "{0} rules selected",
        ["Tool.PortProxy.ConfirmReset"] = "Reset ALL portproxy rules? This cannot be undone.",
        ["Tool.PortProxy.DialogAdd"] = "Add port proxy",
        ["Tool.PortProxy.DialogEdit"] = "Edit port proxy",
        ["Tool.PortProxy.DialogDump"] = "portproxy dump",
        ["Tool.PortProxy.Kind"] = "Mapping kind",
        ["Tool.PortProxy.ListenAddress"] = "Listen address",
        ["Tool.PortProxy.ListenPort"] = "Listen port",
        ["Tool.PortProxy.ConnectAddress"] = "Connect address",
        ["Tool.PortProxy.ConnectPort"] = "Connect port",
        ["Tool.PortProxy.Protocol"] = "Protocol",
        ["Tool.PortProxy.Ok"] = "OK",
        ["Tool.PortProxy.Cancel"] = "Cancel",
        ["Tool.PortProxy.Close"] = "Close",
        ["Tool.PortProxy.ValidationPort"] = "Enter a valid listen port.",
        ["Tool.PortProxy.ValidationConnect"] = "Enter a connect address.",
        ["Tool.PortProxy.Copied"] = "Copied to clipboard",
        ["Tool.PortProxy.NotReady"] = "Port proxy tool is not installed or loaded"
    };
}
