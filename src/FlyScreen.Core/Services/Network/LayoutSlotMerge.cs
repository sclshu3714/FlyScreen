namespace FlyScreen.Services.Network;

/// <summary>布局槽位并集 / 规范化（最多 4 台）。</summary>
public static class LayoutSlotMerge
{
    public const int MaxSlots = 4;

    public static IEnumerable<string> NonEmptyNames(IEnumerable<string?> slots)
    {
        foreach (var s in slots)
        {
            var t = s?.Trim() ?? "";
            if (!string.IsNullOrEmpty(t))
                yield return t;
        }
    }

    public static int CountUnique(IEnumerable<string?> slots)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in NonEmptyNames(slots))
            set.Add(n);
        return set.Count;
    }

    /// <summary>
    /// 合并两套槽位：保留 <paramref name="primary"/> 中已有顺序，再追加 secondary 中尚未出现的名字。
    /// 唯一名超过 <see cref="MaxSlots"/> 时失败。
    /// </summary>
    public static bool TryMerge(
        IReadOnlyList<string?> primary,
        IReadOnlyList<string?> secondary,
        out List<string> result)
    {
        var names = new List<string>(MaxSlots);
        void AddRange(IEnumerable<string?> src)
        {
            foreach (var n in NonEmptyNames(src))
            {
                if (names.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)))
                    continue;
                names.Add(n);
            }
        }

        AddRange(primary);
        AddRange(secondary);
        if (names.Count > MaxSlots)
        {
            result = [];
            return false;
        }

        while (names.Count < MaxSlots)
            names.Add("");
        result = names;
        return true;
    }

    public static List<string> Normalize(IEnumerable<string?> slots)
    {
        var names = NonEmptyNames(slots).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxSlots).ToList();
        while (names.Count < MaxSlots)
            names.Add("");
        return names;
    }

    public static bool ContainsName(IEnumerable<string?> slots, string name)
        => NonEmptyNames(slots).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>布局为空，或唯一非空槽位就是本机。</summary>
    public static bool IsOnlyLocal(IEnumerable<string?> slots, string localMachine)
    {
        var names = NonEmptyNames(slots).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return true;
        return names.Count == 1
               && string.Equals(names[0], localMachine, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameSet(IEnumerable<string?> a, IEnumerable<string?> b)
    {
        var sa = new HashSet<string>(NonEmptyNames(a), StringComparer.OrdinalIgnoreCase);
        var sb = new HashSet<string>(NonEmptyNames(b), StringComparer.OrdinalIgnoreCase);
        return sa.SetEquals(sb);
    }
}

/// <summary><see cref="LayoutSyncPayload.Mode"/> 取值。</summary>
public static class LayoutSyncModes
{
    /// <summary>权威替换（移除电脑后）；不在槽位中的本机视为被踢出。</summary>
    public const int Replace = 0;

    /// <summary>并集合并（添加电脑 / 集群互联）。</summary>
    public const int Merge = 1;

    /// <summary>合并失败（并集将超过 4 台），通知发起方回滚。</summary>
    public const int MergeReject = 2;
}
