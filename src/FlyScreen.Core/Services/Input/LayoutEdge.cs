using FlyScreen.Models;

namespace FlyScreen.Services.Input;

/// <summary>
/// 布局里沿某一边找下一台已占用的机器。空位跳过。
/// 1×4 时 A 的右侧邻居是 B，再向右才是 C、D；2×2 时上下边同样只走一格。
/// </summary>
public static class LayoutEdge
{
    public static int FindIndex(AppSettings settings, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        var n = Math.Min(4, settings.MachineSlots.Count);
        for (var i = 0; i < n; i++)
        {
            if (string.Equals(settings.MachineSlots[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <param name="exitSide">-1 左、+1 右、-2 上、+2 下。</param>
    public static string? Neighbor(AppSettings settings, string? fromMachine, int exitSide)
    {
        var from = FindIndex(settings, fromMachine);
        if (from < 0 || exitSide == 0) return null;
        var horizontal = exitSide is -1 or 1;
        var dir = exitSide < 0 ? -1 : 1;
        return Neighbor(settings, from, horizontal, dir);
    }

    public static string? Neighbor(AppSettings settings, int fromIndex, bool horizontal, int dir)
    {
        if (fromIndex < 0 || dir == 0) return null;

        var slots = new string?[4];
        var n = Math.Min(4, settings.MachineSlots.Count);
        for (var i = 0; i < n; i++)
            slots[i] = settings.MachineSlots[i];

        var cols = settings.TwoRow ? 2 : 4;
        var rows = settings.TwoRow ? 2 : 1;
        if (fromIndex >= cols * rows) return null;
        var row = fromIndex / cols;
        var col = fromIndex % cols;

        var occupied = new List<int>();
        if (horizontal)
        {
            for (var c = 0; c < cols; c++)
            {
                if (!string.IsNullOrWhiteSpace(slots[row * cols + c]))
                    occupied.Add(c);
            }
        }
        else
        {
            for (var r = 0; r < rows; r++)
            {
                if (!string.IsNullOrWhiteSpace(slots[r * cols + col]))
                    occupied.Add(r);
            }
        }

        if (occupied.Count == 0) return null;
        var current = horizontal ? col : row;
        var myPos = occupied.IndexOf(current);
        if (myPos < 0) return null;

        int nextPos;
        if (settings.WrapMouse)
        {
            if (occupied.Count < 2) return null;
            nextPos = (myPos + dir + occupied.Count) % occupied.Count;
        }
        else
        {
            nextPos = myPos + dir;
            if (nextPos < 0 || nextPos >= occupied.Count) return null;
        }

        var target = occupied[nextPos];
        return horizontal
            ? slots[row * cols + target]
            : slots[target * cols + col];
    }
}
