using FlyScreen.Helpers.Localization;

namespace FlyScreen.Tool.FileVault;

public enum VaultSortKey
{
    Name,
    Date,
    Type,
    Size,
    Tags,
    Created,
    Modified,
    DateTaken,
    Resolution,
    Rating
}

public enum VaultSortOrder
{
    Ascending,
    Descending
}

public sealed class VaultSortState
{
    public VaultSortKey Key { get; set; } = VaultSortKey.Name;
    public VaultSortOrder Order { get; set; } = VaultSortOrder.Ascending;
    public VaultSortKey? Group { get; set; }
}

public readonly record struct VaultSortFacts(string Name, bool IsDir, long Size, DateTimeOffset ModifiedUtc);

public readonly record struct VaultArranged<T>(bool IsHeader, string Header, T? Item);

/// <summary>对齐资源管理器的排序和分组。没有的元数据（标记、拍摄日期、分辨率、分级）归到「无」。</summary>
public static class VaultSort
{
    public static readonly VaultSortKey[] Primary = [VaultSortKey.Name, VaultSortKey.Date, VaultSortKey.Type];
    public static readonly VaultSortKey[] More =
    [
        VaultSortKey.Size, VaultSortKey.Tags, VaultSortKey.Created, VaultSortKey.Modified,
        VaultSortKey.DateTaken, VaultSortKey.Resolution, VaultSortKey.Rating
    ];
    public static readonly VaultSortKey[] GroupKeys =
    [
        VaultSortKey.Name, VaultSortKey.Date, VaultSortKey.Type, VaultSortKey.Size, VaultSortKey.Tags,
        VaultSortKey.Created, VaultSortKey.Modified, VaultSortKey.DateTaken, VaultSortKey.Resolution, VaultSortKey.Rating
    ];

    public static string Title(VaultSortKey key) => key switch
    {
        VaultSortKey.Name => Loc.T("Tool.FileVault.SortName"),
        VaultSortKey.Date => Loc.T("Tool.FileVault.SortDate"),
        VaultSortKey.Type => Loc.T("Tool.FileVault.SortType"),
        VaultSortKey.Size => Loc.T("Tool.FileVault.SortSize"),
        VaultSortKey.Tags => Loc.T("Tool.FileVault.SortTags"),
        VaultSortKey.Created => Loc.T("Tool.FileVault.SortCreated"),
        VaultSortKey.Modified => Loc.T("Tool.FileVault.SortModified"),
        VaultSortKey.DateTaken => Loc.T("Tool.FileVault.SortTaken"),
        VaultSortKey.Resolution => Loc.T("Tool.FileVault.SortResolution"),
        VaultSortKey.Rating => Loc.T("Tool.FileVault.SortRating"),
        _ => ""
    };

    public static List<VaultArranged<T>> Arrange<T>(
        IReadOnlyList<T> items,
        Func<T, VaultSortFacts> facts,
        VaultSortState state)
    {
        var list = items.ToList();
        if (state.Group is not VaultSortKey group)
        {
            list.Sort((a, b) => Compare(facts(a), facts(b), state));
            return list.Select(i => new VaultArranged<T>(false, "", i)).ToList();
        }

        var desc = state.Order == VaultSortOrder.Descending;
        var buckets = list
            .GroupBy(i => GroupOf(facts(i), group))
            .OrderBy(g => g.Key, new BucketComparer(group, desc))
            .ToList();

        var result = new List<VaultArranged<T>>(list.Count + buckets.Count);
        foreach (var bucket in buckets)
        {
            result.Add(new VaultArranged<T>(true, bucket.Key.Title, default));
            foreach (var item in bucket.OrderBy(i => facts(i), Comparer<VaultSortFacts>.Create((a, b) => Compare(a, b, state))))
                result.Add(new VaultArranged<T>(false, "", item));
        }
        return result;
    }

    private static int Compare(VaultSortFacts a, VaultSortFacts b, VaultSortState state)
    {
        if (a.IsDir != b.IsDir)
            return a.IsDir ? -1 : 1;
        var cmp = CompareKey(a, b, state.Key);
        if (state.Order == VaultSortOrder.Descending)
            cmp = -cmp;
        if (cmp != 0)
            return cmp;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareKey(VaultSortFacts a, VaultSortFacts b, VaultSortKey key) => key switch
    {
        VaultSortKey.Name => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        VaultSortKey.Date or VaultSortKey.Created or VaultSortKey.Modified => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
        VaultSortKey.Type => string.Compare(TypeName(a), TypeName(b), StringComparison.OrdinalIgnoreCase),
        VaultSortKey.Size => a.Size.CompareTo(b.Size),
        _ => 0
    };

    private static SortBucket GroupOf(VaultSortFacts facts, VaultSortKey key) => key switch
    {
        VaultSortKey.Name => NameBucket(facts.Name),
        VaultSortKey.Date or VaultSortKey.Created or VaultSortKey.Modified => DateBucket(facts.ModifiedUtc),
        VaultSortKey.Type => new SortBucket(0, TypeName(facts)),
        VaultSortKey.Size => SizeBucket(facts.Size),
        _ => new SortBucket(0, Loc.T("Tool.FileVault.SortNone"))
    };

    private static string TypeName(VaultSortFacts facts)
    {
        if (facts.IsDir)
            return Loc.T("Tool.FileVault.TypeFolder");
        var ext = Path.GetExtension(facts.Name);
        return string.IsNullOrEmpty(ext) ? Loc.T("Tool.FileVault.TypeFile") : ext.TrimStart('.').ToUpperInvariant();
    }

    private static SortBucket NameBucket(string name)
    {
        var trimmed = name.TrimStart();
        if (trimmed.Length == 0)
            return new SortBucket(0, Loc.T("Tool.FileVault.SortNone"));
        var ch = trimmed[0];
        if (char.IsDigit(ch))
            return new SortBucket(0, Loc.T("Tool.FileVault.SortDigits"));
        return new SortBucket(0, char.ToUpperInvariant(ch).ToString());
    }

    private static SortBucket DateBucket(DateTimeOffset time)
    {
        var local = time.ToLocalTime().Date;
        var today = DateTime.Today;
        if (local == today) return new SortBucket(8, Loc.T("Tool.FileVault.SortToday"));
        if (local == today.AddDays(-1)) return new SortBucket(7, Loc.T("Tool.FileVault.SortYesterday"));
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (local >= weekStart) return new SortBucket(6, Loc.T("Tool.FileVault.SortEarlierWeek"));
        if (local >= weekStart.AddDays(-7)) return new SortBucket(5, Loc.T("Tool.FileVault.SortLastWeek"));
        var monthStart = new DateTime(today.Year, today.Month, 1);
        if (local >= monthStart) return new SortBucket(4, Loc.T("Tool.FileVault.SortEarlierMonth"));
        if (local >= monthStart.AddMonths(-1)) return new SortBucket(3, Loc.T("Tool.FileVault.SortLastMonth"));
        var yearStart = new DateTime(today.Year, 1, 1);
        if (local >= yearStart) return new SortBucket(2, Loc.T("Tool.FileVault.SortEarlierYear"));
        if (local >= yearStart.AddYears(-1)) return new SortBucket(1, Loc.T("Tool.FileVault.SortLastYear"));
        return new SortBucket(0, Loc.T("Tool.FileVault.SortOlder"));
    }

    private static SortBucket SizeBucket(long size)
    {
        const long kb = 1024;
        if (size <= 0) return new SortBucket(0, Loc.T("Tool.FileVault.SortSizeEmpty"));
        if (size < 16 * kb) return new SortBucket(1, Loc.T("Tool.FileVault.SortSizeTiny"));
        if (size < kb * kb) return new SortBucket(2, Loc.T("Tool.FileVault.SortSizeSmall"));
        if (size < 16 * kb * kb) return new SortBucket(3, Loc.T("Tool.FileVault.SortSizeMedium"));
        if (size < 128L * kb * kb) return new SortBucket(4, Loc.T("Tool.FileVault.SortSizeLarge"));
        return new SortBucket(5, Loc.T("Tool.FileVault.SortSizeHuge"));
    }

    private readonly record struct SortBucket(int Rank, string Title);

    private sealed class BucketComparer(VaultSortKey key, bool descending) : IComparer<SortBucket>
    {
        public int Compare(SortBucket x, SortBucket y)
        {
            var cmp = key is VaultSortKey.Name or VaultSortKey.Type
                ? string.Compare(x.Title, y.Title, StringComparison.OrdinalIgnoreCase)
                : x.Rank.CompareTo(y.Rank);
            return descending ? -cmp : cmp;
        }
    }
}
