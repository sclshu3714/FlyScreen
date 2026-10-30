namespace FlyScreen.Services.ScreenExtend;

/// <summary>
/// 布局中本机相对对端的方位（与 InputRouter peerSide 一致：-1 左 / +1 右 / -2 上 / +2 下）。
/// </summary>
public enum LayoutEdgeSide
{
    Unknown = 0,
    Left = -1,
    Right = 1,
    Top = -2,
    Bottom = 2
}

/// <summary>根据电脑布局槽位，解析「投影目标电脑」相对本机的方向。</summary>
public static class LayoutPeerDirection
{
    /// <summary>
    /// 固定 4 格：单行 [0][1][2][3]；双行 [0][1]/[2][3]。
    /// 返回对端相对本机的方位，供扩展屏物理摆放对齐穿边方向。
    /// </summary>
    public static LayoutEdgeSide Resolve(
        IReadOnlyList<string?> machineSlots,
        bool twoRow,
        string localMachine,
        string peerMachine,
        string? envMachineName = null)
    {
        if (string.IsNullOrWhiteSpace(peerMachine))
            return LayoutEdgeSide.Unknown;

        var slots = NormalizeSlots(machineSlots);
        var localIdx = FindSlotIndex(slots, localMachine);
        if (localIdx < 0 && !string.IsNullOrWhiteSpace(envMachineName))
            localIdx = FindSlotIndex(slots, envMachineName);
        var peerIdx = FindSlotIndex(slots, peerMachine);
        if (localIdx < 0 || peerIdx < 0)
            return LayoutEdgeSide.Unknown;

        var cols = twoRow ? 2 : 4;
        var localRow = localIdx / cols;
        var localCol = localIdx % cols;
        var peerRow = peerIdx / cols;
        var peerCol = peerIdx % cols;

        if (peerRow == localRow && peerCol < localCol) return LayoutEdgeSide.Left;
        if (peerRow == localRow && peerCol > localCol) return LayoutEdgeSide.Right;
        if (peerCol == localCol && peerRow < localRow) return LayoutEdgeSide.Top;
        if (peerCol == localCol && peerRow > localRow) return LayoutEdgeSide.Bottom;

        // 不相邻：按主轴近似
        if (Math.Abs(peerCol - localCol) >= Math.Abs(peerRow - localRow))
            return peerCol < localCol ? LayoutEdgeSide.Left : LayoutEdgeSide.Right;
        return peerRow < localRow ? LayoutEdgeSide.Top : LayoutEdgeSide.Bottom;
    }

    private static string?[] NormalizeSlots(IReadOnlyList<string?> machineSlots)
    {
        var slots = new string?[4];
        for (var i = 0; i < 4; i++)
        {
            var name = i < machineSlots.Count ? machineSlots[i] : null;
            slots[i] = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        return slots;
    }

    private static int FindSlotIndex(string?[] slots, string name)
    {
        for (var i = 0; i < slots.Length; i++)
        {
            if (string.Equals(slots[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
