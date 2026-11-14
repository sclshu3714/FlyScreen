using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>导入模式。</summary>
public enum SourceImportMode
{
    /// <summary>合并：新 Id 写入；地址重复则跳过。</summary>
    Merge,

    /// <summary>替换：清空现有源与分组后再导入。</summary>
    Replace
}

/// <summary>导入统计。</summary>
public sealed record SourceImportResult(int Added, int Skipped, int Failed, IReadOnlyList<string> Messages);

/// <summary>
/// 固定源清单导入/导出。默认不含凭据与封面（docs §11、prototype §13）。
/// </summary>
public static class SourceImportExport
{
    private const int ExportSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>导出 JSON；默认剥离 <c>credentialRef</c> 与封面字段。</summary>
    public static async Task ExportAsync(
        VideoSourceCatalog catalog,
        string outputPath,
        bool includeCredentials = false,
        CredentialStore? credentials = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var export = new ExportFile
        {
            SchemaVersion = ExportSchemaVersion,
            Groups = catalog.Groups.Select(g => new ExportGroup { Id = g.Id, Name = g.Name }).ToList(),
            Sources = catalog.Sources.Select(s => ToExportDto(s, includeCredentials)).ToList()
        };

        if (includeCredentials && credentials is not null)
        {
            export.Credentials = [];
            foreach (var source in catalog.Sources)
            {
                var r = source.Endpoint.CredentialRef;
                if (string.IsNullOrWhiteSpace(r) || export.Credentials.Any(c => c.Id == r))
                    continue;

                var cred = credentials.Get(r);
                if (cred is null) continue;

                export.Credentials.Add(new ExportCredential
                {
                    Id = r,
                    Username = cred.Username,
                    Password = cred.Password,
                    AuthKind = cred.AuthKind
                });
            }
        }

        var json = JsonSerializer.Serialize(export, JsonOptions);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(outputPath, json, ct).ConfigureAwait(false);
    }

    /// <summary>从 JSON 导入到目录；支持合并去重。</summary>
    public static async Task<SourceImportResult> ImportAsync(
        VideoSourceCatalog catalog,
        string inputPath,
        SourceImportMode mode,
        CredentialStore? credentials = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        if (!File.Exists(inputPath))
            throw new FileNotFoundException("找不到导入文件", inputPath);

        var json = await File.ReadAllTextAsync(inputPath, ct).ConfigureAwait(false);
        var file = JsonSerializer.Deserialize<ExportFile>(json, JsonOptions)
                   ?? throw new InvalidDataException("导入文件为空或格式错误");

        if (file.SchemaVersion > ExportSchemaVersion)
            throw new InvalidDataException($"导入版本 {file.SchemaVersion} 高于支持版本 {ExportSchemaVersion}");

        var messages = new List<string>();
        var added = 0;
        var skipped = 0;
        var failed = 0;

        if (mode == SourceImportMode.Replace)
        {
            foreach (var id in catalog.Sources.Select(s => s.Id).ToList())
                catalog.Remove(id);
        }

        if (file.Credentials is { Count: > 0 } && credentials is not null)
        {
            foreach (var c in file.Credentials)
            {
                if (string.IsNullOrWhiteSpace(c.Id)) continue;
                credentials.Save(c.Id, c.Username, c.Password, c.AuthKind);
            }

            credentials.Save();
        }

        foreach (var g in file.Groups)
        {
            if (string.IsNullOrWhiteSpace(g.Name)) continue;
            catalog.EnsureGroup(g.Name);
        }

        foreach (var dto in file.Sources)
        {
            try
            {
                var descriptor = FromExportDto(dto);
                var result = catalog.Add(descriptor);
                if (result.Added)
                {
                    added++;
                }
                else
                {
                    skipped++;
                    messages.Add(result.Reason);
                }
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"导入 {dto.Id ?? dto.Name}: {ex.Message}");
            }
        }

        await catalog.SaveAsync(ct).ConfigureAwait(false);
        return new SourceImportResult(added, skipped, failed, messages);
    }

    private static ExportSourceDto ToExportDto(VideoSourceDescriptor source, bool includeCredentials)
    {
        return new ExportSourceDto
        {
            Id = source.Id,
            Name = source.Name,
            Kind = source.Kind.ToString(),
            Origin = source.Origin.ToString(),
            GroupId = source.GroupId,
            Protocol = source.Endpoint.Protocol,
            Address = source.Endpoint.Address,
            Transport = source.Endpoint.Transport,
            BufferMs = source.Endpoint.BufferMs,
            Headers = source.Endpoint.Headers.Count > 0
                ? new Dictionary<string, string>(source.Endpoint.Headers)
                : null,
            CredentialRef = includeCredentials ? source.Endpoint.CredentialRef : null,
            Decode = source.DecodePreference,
            Note = source.Note,
            ValidationState = source.ValidationState.ToString(),
            FailureCount = source.FailureCount,
            CreatedAt = source.CreatedAt
        };
    }

    private static VideoSourceDescriptor FromExportDto(ExportSourceDto dto)
    {
        Enum.TryParse<VideoSourceKind>(dto.Kind, ignoreCase: true, out var kind);
        Enum.TryParse<VideoSourceOrigin>(dto.Origin, ignoreCase: true, out var origin);
        Enum.TryParse<SourceValidationState>(dto.ValidationState, ignoreCase: true, out var validation);

        return new VideoSourceDescriptor
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? VideoSourceCatalog.NewId() : dto.Id,
            Name = dto.Name,
            Kind = kind,
            Origin = origin,
            GroupId = dto.GroupId,
            DecodePreference = dto.Decode ?? "auto",
            Note = dto.Note,
            ValidationState = validation,
            FailureCount = dto.FailureCount,
            CoverKey = null,
            CoverCapturedAt = null,
            CreatedAt = dto.CreatedAt ?? DateTimeOffset.Now,
            Endpoint = new VideoEndpoint
            {
                Protocol = dto.Protocol,
                Address = dto.Address,
                Transport = dto.Transport,
                BufferMs = dto.BufferMs,
                Headers = dto.Headers ?? new Dictionary<string, string>(),
                CredentialRef = dto.CredentialRef
            }
        };
    }

    private sealed class ExportFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ExportGroup> Groups { get; set; } = [];
        public List<ExportSourceDto> Sources { get; set; } = [];
        public List<ExportCredential>? Credentials { get; set; }
    }

    private sealed class ExportGroup
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class ExportSourceDto
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Origin { get; set; } = "Fixed";
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
        public DateTimeOffset? CreatedAt { get; set; }
    }

    private sealed class ExportCredential
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string AuthKind { get; set; } = SourceFormSchema.AuthNone;
    }
}