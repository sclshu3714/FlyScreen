using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Wall;

namespace FlyScreen.Tool.VideoWall.Sources.Dlna;

/// <summary>把 DLNA 视频条目批量加为 NetworkUrl（仅入库，不自动挂墙/开播）。</summary>
public sealed class DlnaImportService
{
    public async Task<DlnaImportResult> ImportAsync(
        IVideoWallService service,
        DlnaServer server,
        IReadOnlyList<DlnaNode> videos,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(videos);

        var playable = videos
            .Where(v => !v.IsFolder && !string.IsNullOrWhiteSpace(v.HttpUrl))
            .GroupBy(v => v.HttpUrl!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (playable.Count == 0)
        {
            return new DlnaImportResult(0, 0, 0, CountEmptySlots(service), "没有可导入的视频");
        }

        var groupName = $"DLNA / {server.FriendlyName}";
        var descriptors = playable.Select(item => BuildSource(server, item, groupName)).ToList();
        var results = await service.AddSourcesAsync(descriptors, ct).ConfigureAwait(false);

        var added = 0;
        var reused = 0;
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result.Added)
                added++;
            else if (result.Existing is not null)
                reused++;
        }

        var emptyBefore = CountEmptySlots(service);
        var message = $"已添加 {added} 部，复用 {reused} 部";
        return new DlnaImportResult(added, reused, 0, emptyBefore, message);
    }

    public static VideoSourceDescriptor BuildSource(DlnaServer server, DlnaNode item, string? groupName = null)
    {
        if (string.IsNullOrWhiteSpace(item.HttpUrl))
            throw new ArgumentException("条目没有 HTTP 地址", nameof(item));

        var parse = VideoEndpointParser.ParseAndNormalize(item.HttpUrl);
        if (!parse.Ok || parse.Endpoint is null)
            throw new InvalidOperationException(parse.Message ?? "无法解析 DLNA 地址");

        // DLNA HTTP：软件帧路径仍强制软解；缓冲 1.2s 兼顾跟手与 NAS 抖动
        var endpoint = parse.Endpoint with { BufferMs = parse.Endpoint.BufferMs ?? 1200 };

        return new VideoSourceDescriptor
        {
            Id = VideoSourceCatalog.NewId(),
            Name = string.IsNullOrWhiteSpace(item.Title) ? "DLNA 视频" : item.Title,
            Kind = VideoSourceKind.NetworkUrl,
            Origin = VideoSourceOrigin.Fixed,
            Endpoint = endpoint,
            GroupId = string.IsNullOrWhiteSpace(groupName) ? $"DLNA / {server.FriendlyName}" : groupName,
            DecodePreference = "software",
            Note = $"DLNA · {server.FriendlyName}",
            ValidationState = SourceValidationState.Unknown
        };
    }

    public static int CountEmptySlots(IVideoWallService service)
        => ListEmptySlotIndexes(service).Count;

    public static IReadOnlyList<int> ListEmptySlotIndexes(IVideoWallService service)
    {
        var tiles = service.Tiles;
        var count = Math.Min(service.Layout.TileCount, tiles.Count);
        var ids = new string?[count];
        for (var i = 0; i < count; i++)
            ids[i] = tiles[i].SourceId;
        return ListEmptySlotIndexes(count, ids);
    }

    /// <summary>按格内 SourceId 计算空位（单元测试与导入共用）。</summary>
    public static IReadOnlyList<int> ListEmptySlotIndexes(int tileCount, IReadOnlyList<string?> sourceIds)
    {
        var list = new List<int>();
        var count = Math.Min(tileCount, sourceIds.Count);
        for (var i = 0; i < count; i++)
        {
            if (string.IsNullOrWhiteSpace(sourceIds[i]))
                list.Add(i);
        }

        return list;
    }
}
