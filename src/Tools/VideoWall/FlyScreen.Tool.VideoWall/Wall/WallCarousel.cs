namespace FlyScreen.Tool.VideoWall.Wall;

/// <summary>
/// 画布轮播分页：按布局格数切页（如 2×2 + 5 源 → 2 页；1×1 → 5 页）。
/// </summary>
public sealed class WallCarousel
{
    public const int DefaultIntervalMs = 5000;

    private readonly List<string> _sourceIds = [];
    private int _pageSize = 4;
    private int _page;
    private bool _enabled;

    public bool Enabled => _enabled;

    public int IntervalMs { get; set; } = DefaultIntervalMs;

    public int CurrentPage => _page;

    public int PageCount
    {
        get
        {
            if (_pageSize <= 0 || _sourceIds.Count == 0)
                return 1;
            return Math.Max(1, (int)Math.Ceiling(_sourceIds.Count / (double)_pageSize));
        }
    }

    public IReadOnlyList<string> SourceIds => _sourceIds;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
            _page = 0;
        ClampPage();
    }

    public void SetPageSize(int pageSize)
    {
        _pageSize = Math.Max(1, pageSize);
        ClampPage();
    }

    public void SetSources(IEnumerable<string> sourceIds)
    {
        _sourceIds.Clear();
        foreach (var id in sourceIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
                _sourceIds.Add(id);
        }

        ClampPage();
    }

    public void ResetPage() => _page = 0;

    public void NextPage()
    {
        var pages = PageCount;
        if (pages <= 1)
        {
            _page = 0;
            return;
        }

        _page = (_page + 1) % pages;
    }

    public void PrevPage()
    {
        var pages = PageCount;
        if (pages <= 1)
        {
            _page = 0;
            return;
        }

        _page = (_page - 1 + pages) % pages;
    }

    public void GoToPage(int page)
    {
        _page = page;
        ClampPage();
    }

    /// <summary>当前页各格的源 Id；空格为 null。</summary>
    public IReadOnlyList<string?> GetPageSlots()
    {
        var slots = new string?[_pageSize];
        var start = _page * _pageSize;
        for (var i = 0; i < _pageSize; i++)
        {
            var idx = start + i;
            slots[i] = idx < _sourceIds.Count ? _sourceIds[idx] : null;
        }

        return slots;
    }

    private void ClampPage()
    {
        var max = Math.Max(0, PageCount - 1);
        if (_page > max)
            _page = max;
        if (_page < 0)
            _page = 0;
    }
}
