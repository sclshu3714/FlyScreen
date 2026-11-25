namespace FlyScreen.Tool.VideoWall.Sources.Dlna;

/// <summary>SSDP 发现到的 DLNA MediaServer。</summary>
public sealed record DlnaServer(
    string Udn,
    string FriendlyName,
    Uri Location,
    string? Manufacturer,
    string? ModelName);

/// <summary>ContentDirectory 浏览节点（文件夹或可播放条目）。</summary>
public sealed record DlnaNode(
    string Id,
    string ParentId,
    bool IsFolder,
    string Title,
    string? HttpUrl,
    string? Mime,
    long? SizeBytes,
    string? UpnpClass);

/// <summary>单页 Browse 结果。</summary>
public sealed record DlnaBrowsePage(
    IReadOnlyList<DlnaNode> Nodes,
    int NumberReturned,
    int TotalMatches,
    bool HasMore);

/// <summary>批量导入结果。</summary>
public sealed record DlnaImportResult(
    int AddedSources,
    int ReusedSources,
    int BoundTiles,
    int EmptySlotsBefore,
    string Message);
