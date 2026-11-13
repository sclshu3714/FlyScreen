using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>添加结果（去重失败时 <see cref="Added"/> 为 false 并给出原因）。</summary>
public sealed record AddSourceResult(bool Added, string Reason, VideoSourceDescriptor? Existing);

/// <summary>
/// 固定源目录：CRUD + JSON 持久化 + 地址去重。对应 <c>video-wall.sources.json</c>
/// （结构见 docs/video-wall-prototype.md §11.1）。
/// 本类不做播放、不抓封面；封面由 <see cref="CoverCache"/> 与后端负责。
/// </summary>
public sealed class VideoSourceCatalog
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<VideoSourceDescriptor> _sources = [];
    private readonly List<VideoSourceGroup> _groups = [];

    public VideoSourceCatalog(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath 不能为空", nameof(filePath));

        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public IReadOnlyList<VideoSourceDescriptor> Sources => _sources;

    public IReadOnlyList<VideoSourceGroup> Groups => _groups;

    /// <summary>读取；文件不存在视为空目录，不创建文件。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _sources.Clear();
        _groups.Clear();

        if (!File.Exists(FilePath)) return;

        var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
        var file = JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions)
                   ?? throw new InvalidDataException($"源目录为空或格式错误：{FilePath}");

        if (file.SchemaVersion > SchemaVersion)
            throw new InvalidDataException($"源目录版本 {file.SchemaVersion} 高于支持版本 {SchemaVersion}");

        foreach (var g in file.Groups)
            _groups.Add(new VideoSourceGroup { Id = g.Id, Name = g.Name });

        foreach (var s in file.Sources)
            _sources.Add(ToDescriptor(s));
    }

    /// <summary>原子写入：先写临时文件再替换，避免断电/崩溃留下半个 JSON。</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new CatalogFile
        {
            SchemaVersion = SchemaVersion,
            Groups = _groups.Select(g => new GroupDto { Id = g.Id, Name = g.Name }).ToList(),
            Sources = _sources.Select(ToDto).ToList()
        };

        var json = JsonSerializer.Serialize(file, JsonOptions);
        var temp = FilePath + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>添加一条；同一协议+传输+地址已存在时拒绝（不静默去重）。</summary>
    public AddSourceResult Add(VideoSourceDescriptor source)
    {
        var existing = FindDuplicate(source.Endpoint);
        if (existing != null)
            return new AddSourceResult(false, DuplicateReason(existing), existing);

        var normalized = EnsureId(source);
        if (_sources.Any(s => s.Id == normalized.Id))
            return new AddSourceResult(false, $"Id 已存在：{normalized.Id}", null);

        _sources.Add(normalized);
        return new AddSourceResult(true, "", null);
    }

    /// <summary>编辑保存：按 Id 替换；改了地址则重新查重。</summary>
    public AddSourceResult Update(VideoSourceDescriptor source)
    {
        var index = _sources.FindIndex(s => s.Id == source.Id);
        if (index < 0)
            return new AddSourceResult(false, $"源不存在：{source.Id}", null);

        var existing = FindDuplicate(source.Endpoint, exceptId: source.Id);
        if (existing != null)
            return new AddSourceResult(false, DuplicateReason(existing), existing);

        _sources[index] = source;
        return new AddSourceResult(true, "", null);
    }

    /// <summary>更新校验结果（抓帧/播放回写用），不触发去重。</summary>
    public bool UpdateValidation(
        string id,
        SourceValidationState state,
        string? coverKey = null,
        DateTimeOffset? coverCapturedAt = null,
        DateTimeOffset? lastPlayedAt = null)
    {
        var index = _sources.FindIndex(s => s.Id == id);
        if (index < 0) return false;

        var current = _sources[index];
        _sources[index] = current with
        {
            ValidationState = state,
            FailureCount = state == SourceValidationState.Ok ? 0 : current.FailureCount + 1,
            CoverKey = coverKey ?? current.CoverKey,
            CoverCapturedAt = coverCapturedAt ?? current.CoverCapturedAt,
            LastPlayedAt = lastPlayedAt ?? current.LastPlayedAt
        };
        return true;
    }

    public bool Remove(string id)
        => _sources.RemoveAll(s => s.Id == id) > 0;

    /// <summary>查找与给定端点重复的源；<paramref name="exceptId"/> 用于编辑时排除自身。</summary>
    public VideoSourceDescriptor? FindDuplicate(VideoEndpoint endpoint, string? exceptId = null)
    {
        var key = BuildDedupeKey(endpoint);
        return _sources.FirstOrDefault(s =>
            s.Id != exceptId && string.Equals(BuildDedupeKey(s.Endpoint), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 去重键：协议 + 传输方式 + 归一化地址。凭据与自定义 Header 不参与
    /// （同地址不同账号在 UI 上仍视为同一条，避免「复制一条只改密码」）。
    /// </summary>
    public static string BuildDedupeKey(VideoEndpoint endpoint)
    {
        var protocol = (endpoint.Protocol ?? "").Trim().ToLowerInvariant();
        var transport = (endpoint.Transport ?? "").Trim().ToLowerInvariant();
        var address = (endpoint.Address ?? "").Trim();
        return $"{protocol}|{transport}|{address}";
    }

    public VideoSourceGroup EnsureGroup(string name)
    {
        var trimmed = name.Trim();
        var existing = _groups.FirstOrDefault(g => string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var group = new VideoSourceGroup { Id = NewId("grp"), Name = trimmed };
        _groups.Add(group);
        return group;
    }

    /// <summary>新建源 Id：<c>src-xxxxxxxx</c>。</summary>
    public static string NewId(string prefix = "src")
        => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static VideoSourceDescriptor EnsureId(VideoSourceDescriptor source)
        => string.IsNullOrWhiteSpace(source.Id) ? source with { Id = NewId() } : source;

    private static string DuplicateReason(VideoSourceDescriptor existing)
        => $"已存在相同地址：{existing.Name}（{existing.Endpoint.Protocol}）；如确需两条，请修改其中一条的地址或传输方式。";

    private static VideoSourceDescriptor ToDescriptor(SourceDto d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Kind = ParseEnum<VideoSourceKind>(d.Kind, d.Id),
        Origin = ParseEnum<VideoSourceOrigin>(d.Origin, d.Id),
        GroupId = d.GroupId,
        DecodePreference = d.Decode ?? "auto",
        Note = d.Note,
        ValidationState = ParseEnum(d.ValidationState, SourceValidationState.Unknown, d.Id),
        FailureCount = d.FailureCount,
        CoverKey = d.CoverKey,
        CoverCapturedAt = d.CoverCapturedAt,
        LastPlayedAt = d.LastPlayedAt,
        CreatedAt = d.CreatedAt ?? DateTimeOffset.Now,
        Endpoint = new VideoEndpoint
        {
            Protocol = d.Protocol,
            Address = d.Address,
            Transport = d.Transport,
            BufferMs = d.BufferMs,
            Headers = d.Headers ?? new Dictionary<string, string>(),
            CredentialRef = d.CredentialRef
        }
    };

    private static SourceDto ToDto(VideoSourceDescriptor s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Kind = s.Kind.ToString(),
        Origin = s.Origin.ToString(),
        GroupId = s.GroupId,
        Protocol = s.Endpoint.Protocol,
        Address = s.Endpoint.Address,
        Transport = s.Endpoint.Transport,
        BufferMs = s.Endpoint.BufferMs,
        Headers = s.Endpoint.Headers.Count > 0 ? new Dictionary<string, string>(s.Endpoint.Headers) : null,
        CredentialRef = s.Endpoint.CredentialRef,
        Decode = s.DecodePreference,
        Note = s.Note,
        ValidationState = s.ValidationState.ToString(),
        FailureCount = s.FailureCount,
        CoverKey = s.CoverKey,
        CoverCapturedAt = s.CoverCapturedAt,
        LastPlayedAt = s.LastPlayedAt,
        CreatedAt = s.CreatedAt
    };

    private static TEnum ParseEnum<TEnum>(string? value, string id) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)) return parsed;
        throw new InvalidDataException($"源 {id} 的 {typeof(TEnum).Name} 取值非法：{value ?? "<null>"}");
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string id) where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private sealed class CatalogFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<GroupDto> Groups { get; set; } = [];
        public List<SourceDto> Sources { get; set; } = [];
    }

    private sealed class GroupDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class SourceDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Origin { get; set; } = "";
        public string? GroupId { get; set; }
        public string Protocol { get; set; } = "";
        public string Address { get; set; } = "";
        public string? Transport { get; set; }
        public int? BufferMs { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string? CredentialRef { get; set; }
        public string? Decode { get; set; }
        public string? Note { get; set; }
        public string? ValidationState { get; set; }
        public int FailureCount { get; set; }
        public string? CoverKey { get; set; }
        public DateTimeOffset? CoverCapturedAt { get; set; }
        public DateTimeOffset? LastPlayedAt { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
    }
}
