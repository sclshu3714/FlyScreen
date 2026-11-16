using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>最近使用的临时源条目。</summary>
public sealed record RecentSourceEntry(
    string Name,
    VideoEndpoint Endpoint,
    DateTimeOffset UsedAt);

/// <summary>
/// 最近 20 条临时源：<c>video-wall.recent.json</c>。签名 URL 落盘前脱敏。
/// </summary>
public sealed class RecentSourcesStore
{
    public const int MaxEntries = 20;
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<RecentSourceEntry> _entries = [];

    public RecentSourcesStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath 不能为空", nameof(filePath));

        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public IReadOnlyList<RecentSourceEntry> Entries => _entries;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _entries.Clear();
        if (!File.Exists(FilePath)) return;

        var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
        var file = JsonSerializer.Deserialize<RecentFile>(json, JsonOptions);
        if (file?.Entries is null) return;

        foreach (var e in file.Entries)
        {
            _entries.Add(new RecentSourceEntry(
                e.Name,
                new VideoEndpoint
                {
                    Protocol = e.Protocol,
                    Address = e.Address,
                    Transport = e.Transport,
                    BufferMs = e.BufferMs,
                    CredentialRef = e.CredentialRef
                },
                e.UsedAt));
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new RecentFile
        {
            SchemaVersion = SchemaVersion,
            Entries = _entries.Select(ToDto).ToList()
        };

        var json = JsonSerializer.Serialize(file, JsonOptions);
        var temp = FilePath + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>记录一次使用；同地址协议则更新时间与名称并置顶。</summary>
    public async Task TouchAsync(string name, VideoEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var sanitized = SanitizeEndpoint(endpoint);
        var key = VideoSourceCatalog.BuildDedupeKey(sanitized);

        var existing = _entries.FindIndex(e => string.Equals(
            VideoSourceCatalog.BuildDedupeKey(e.Endpoint),
            key,
            StringComparison.OrdinalIgnoreCase));

        var now = DateTimeOffset.Now;
        if (existing >= 0)
        {
            _entries.RemoveAt(existing);
            _entries.Insert(0, new RecentSourceEntry(name, sanitized, now));
        }
        else
        {
            _entries.Insert(0, new RecentSourceEntry(name, sanitized, now));
        }

        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);

        await SaveAsync(ct).ConfigureAwait(false);
    }

    private static VideoEndpoint SanitizeEndpoint(VideoEndpoint endpoint)
    {
        var address = endpoint.Address ?? "";
        var parse = VideoEndpointParser.ParseAndNormalize(address);
        var redacted = VideoEndpointParser.RedactAddress(
            parse.Ok && parse.Endpoint is not null ? parse.Endpoint.Address : address);

        return endpoint with { Address = redacted };
    }

    private static RecentEntryDto ToDto(RecentSourceEntry e) => new()
    {
        Name = e.Name,
        Protocol = e.Endpoint.Protocol,
        Address = e.Endpoint.Address,
        Transport = e.Endpoint.Transport,
        BufferMs = e.Endpoint.BufferMs,
        CredentialRef = e.Endpoint.CredentialRef,
        UsedAt = e.UsedAt
    };

    private sealed class RecentFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<RecentEntryDto> Entries { get; set; } = [];
    }

    private sealed class RecentEntryDto
    {
        public string Name { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string Address { get; set; } = "";
        public string? Transport { get; set; }
        public int? BufferMs { get; set; }
        public string? CredentialRef { get; set; }
        public DateTimeOffset UsedAt { get; set; }
    }
}