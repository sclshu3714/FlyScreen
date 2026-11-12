using System.Collections.ObjectModel;
using FlyScreen.Services.Network;

namespace FlyScreen.Models;

/// <summary>
/// 设备管理全量清单行（本地 / 远程 / 服务）。纯 UI 模型，不进协议。
/// </summary>
public sealed class DeviceInventoryItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>0=本地 1=远程 2=服务。</summary>
    public int Kind { get; set; }
    public string Address { get; set; } = "—";
    public string StatusText { get; set; } = "";
    public bool IsOnline { get; set; }
    public bool IsLocalMachine { get; set; }
    /// <summary>本地：布局槽位索引；-1 未加入。</summary>
    public int LayoutSlotIndex { get; set; } = -1;
    public string LayoutPositionLabel { get; set; } = "";
    /// <summary>远程：是否已绑定到布局（旧路径；设备管理直接投影不再依赖）。</summary>
    public bool IsBound { get; set; }
    /// <summary>远程：当前是否正在向本机投影。</summary>
    public bool IsProjecting { get; set; }
    public string BoundPositionLabel { get; set; } = "";
    public string BoundLocalDeviceId { get; set; } = "";
    /// <summary>服务：ConnectionEndpoint.Id。</summary>
    public string EndpointId { get; set; } = "";
    public int? RttMs { get; set; }
    public DiscoveredPeer? Peer { get; set; }

    public const int KindLocal = 0;
    public const int KindRemote = 1;
    public const int KindService = 2;

    public string KindLabel => Kind switch
    {
        KindRemote => "远程",
        KindService => "服务",
        _ => "本地"
    };

    /// <summary>主操作按钮文案（ToolTip / 无障碍）。</summary>
    public string PrimaryActionText
    {
        get
        {
            if (Kind == KindLocal)
            {
                if (IsLocalMachine) return "定位";
                return LayoutSlotIndex >= 0 ? "定位" : "添加";
            }
            if (Kind is KindRemote or KindService)
                return IsProjecting ? "停止投影" : "投影";
            return "测试";
        }
    }

    public string SecondaryActionText
    {
        get
        {
            if (Kind == KindLocal && LayoutSlotIndex >= 0 && !IsLocalMachine)
                return "移除";
            // 设备管理直接投影不再走布局绑定；旧绑定仍可「解除」
            if ((Kind == KindRemote || Kind == KindService) && IsBound && !IsProjecting)
                return "解除";
            return "";
        }
    }

    /// <summary>主操作图标字符。</summary>
    public string PrimaryActionIcon
    {
        get
        {
            if (Kind == KindLocal)
                return LayoutSlotIndex >= 0 || IsLocalMachine ? "⌖" : "+";
            if (Kind is KindRemote or KindService)
                return IsProjecting ? "■" : "◎";
            return "⚡";
        }
    }

    public string SecondaryActionIcon
    {
        get
        {
            if (string.IsNullOrEmpty(SecondaryActionText)) return "";
            return "−";
        }
    }

    public bool PrimaryActionVisible =>
        Kind != KindLocal || !IsLocalMachine || LayoutSlotIndex >= 0;

    public bool SecondaryActionVisible => !string.IsNullOrEmpty(SecondaryActionText);

    /// <summary>仅一个操作时，主按钮拉满与双按钮同宽。</summary>
    public bool PrimaryActionStretch => PrimaryActionVisible && !SecondaryActionVisible;

    /// <summary>操作区 3 列网格中主按钮的 ColumnSpan（单钮=3，双钮=1）。</summary>
    public int PrimaryActionColumnSpan => PrimaryActionStretch ? 3 : 1;

    /// <summary>
    /// 内容未变则跳过 Clear/Add，避免设备列表整表重绑闪烁、点击落空。
    /// </summary>
    public static bool ReplaceIfChanged(
        ObservableCollection<DeviceInventoryItem> dest,
        IReadOnlyList<DeviceInventoryItem> next)
    {
        if (RowsEqual(dest, next))
            return false;

        dest.Clear();
        foreach (var item in next)
            dest.Add(item);
        return true;
    }

    private static bool RowsEqual(
        IReadOnlyList<DeviceInventoryItem> a,
        IReadOnlyList<DeviceInventoryItem> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (!string.Equals(x.Id, y.Id, StringComparison.Ordinal)
                || !string.Equals(x.Name, y.Name, StringComparison.Ordinal)
                || x.Kind != y.Kind
                || !string.Equals(x.Address, y.Address, StringComparison.Ordinal)
                || !string.Equals(x.StatusText, y.StatusText, StringComparison.Ordinal)
                || x.IsOnline != y.IsOnline
                || x.IsBound != y.IsBound
                || x.IsProjecting != y.IsProjecting
                || x.LayoutSlotIndex != y.LayoutSlotIndex
                || !string.Equals(x.PrimaryActionText, y.PrimaryActionText, StringComparison.Ordinal)
                || !string.Equals(x.SecondaryActionText, y.SecondaryActionText, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
