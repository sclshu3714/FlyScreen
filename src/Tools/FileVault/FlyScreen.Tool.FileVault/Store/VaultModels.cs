using System.Text.Json.Serialization;

namespace FlyScreen.Tool.FileVault.Store;

public sealed class VaultManifestDto
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("vaultId")]
    public string VaultId { get; set; } = "";

    [JsonPropertyName("createdUtc")]
    public string CreatedUtc { get; set; } = "";

    [JsonPropertyName("kdf")]
    public string Kdf { get; set; } = "PBKDF2-SHA256";

    [JsonPropertyName("pbkdf2Iterations")]
    public int Pbkdf2Iterations { get; set; } = Crypto.VaultCrypto.DefaultPbkdf2Iterations;

    [JsonPropertyName("saltBase64")]
    public string SaltBase64 { get; set; } = "";

    [JsonPropertyName("chunkSize")]
    public int ChunkSize { get; set; } = Crypto.VaultCrypto.DefaultChunkSize;

    [JsonPropertyName("entryCount")]
    public int EntryCount { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }
}

public sealed class VaultIndexDto
{
    [JsonPropertyName("entries")]
    public List<VaultEntryDto> Entries { get; set; } = [];
}

public sealed class VaultEntryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("isDir")]
    public bool IsDir { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("modifiedUtc")]
    public string ModifiedUtc { get; set; } = "";

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("dekWrappedBase64")]
    public string? DekWrappedBase64 { get; set; }

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; set; }
}

public sealed class VaultSettingsDto
{
    [JsonPropertyName("activeVaultRoot")]
    public string? ActiveVaultRoot { get; set; }

    /// <summary>解锁后是否自动全量物化挂载；默认 false（大库慢）。</summary>
    [JsonPropertyName("autoMount")]
    public bool AutoMount { get; set; } = false;

    [JsonPropertyName("idleLockMinutes")]
    public int IdleLockMinutes { get; set; } = 10;

    [JsonPropertyName("secureDeleteSource")]
    public bool SecureDeleteSource { get; set; } = true;
}

public enum VaultUninstallAction
{
    Cancel = 0,
    DecryptAndKeep = 1,
    Destroy = 2,
    KeepEncryptedUninstallTool = 3
}

public sealed class VaultEntryView
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string Name { get; init; }
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
}

public sealed class VaultProgress
{
    public string Phase { get; init; } = "";
    public string? CurrentName { get; init; }
    public long Done { get; init; }
    public long Total { get; init; }
}
