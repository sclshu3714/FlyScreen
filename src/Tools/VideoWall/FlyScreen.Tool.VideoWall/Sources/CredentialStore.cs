using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>凭据表中的一条记录。</summary>
public sealed record StoredCredential(string Username, string Password, string AuthKind);

/// <summary>
/// 凭据表：<c>video-wall.credentials.bin</c>，JSON 映射经加密落盘。
/// Windows 使用 DPAPI；其他平台使用本机 AES 密钥文件。
/// </summary>
public sealed class CredentialStore
{
    private const string KeyFileName = "video-wall.credentials.key";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Dictionary<string, StoredCredential> _entries = new(StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly string? _keyFilePath;

    public CredentialStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath 不能为空", nameof(filePath));

        _filePath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(_filePath);
        _keyFilePath = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, KeyFileName);
    }

    public string FilePath => _filePath;

    /// <summary>读取凭据表；文件不存在视为空表。</summary>
    public void Load()
    {
        _entries.Clear();
        if (!File.Exists(_filePath)) return;

        var encrypted = File.ReadAllBytes(_filePath);
        if (encrypted.Length == 0) return;

        var plain = Unprotect(encrypted);
        var file = JsonSerializer.Deserialize<CredentialFile>(plain, JsonOptions)
                   ?? throw new InvalidDataException($"凭据表格式错误：{_filePath}");

        foreach (var (id, dto) in file.Entries)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            _entries[id] = new StoredCredential(dto.Username, dto.Password, dto.AuthKind);
        }
    }

    /// <summary>原子写入加密凭据表。</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new CredentialFile
        {
            Entries = _entries.ToDictionary(
                kv => kv.Key,
                kv => new CredentialDto
                {
                    Username = kv.Value.Username,
                    Password = kv.Value.Password,
                    AuthKind = kv.Value.AuthKind
                },
                StringComparer.Ordinal)
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
        var encrypted = Protect(json);
        var temp = _filePath + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, _filePath, overwrite: true);
    }

    /// <summary>保存凭据；<paramref name="credentialRef"/> 为空时自动生成新 Id。</summary>
    public string Save(string? credentialRef, string username, string password, string authKind)
    {
        var id = string.IsNullOrWhiteSpace(credentialRef) ? NewRef() : credentialRef.Trim();
        _entries[id] = new StoredCredential(username, password, authKind);
        return id;
    }

    public StoredCredential? Get(string credentialRef)
    {
        if (string.IsNullOrWhiteSpace(credentialRef)) return null;
        return _entries.TryGetValue(credentialRef, out var c) ? c : null;
    }

    public bool Delete(string credentialRef)
    {
        if (string.IsNullOrWhiteSpace(credentialRef)) return false;
        return _entries.Remove(credentialRef);
    }

    /// <summary>新建凭据引用键：<c>cred-xxxxxxxx</c>。</summary>
    public static string NewRef()
        => $"cred-{Guid.NewGuid():N}"[..12];

    private byte[] Protect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
            return ProtectDpapi(data);

        return ProtectAes(data);
    }

    private byte[] Unprotect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
            return UnprotectDpapi(data);

        return UnprotectAes(data);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectDpapi(byte[] data)
    {
#pragma warning disable CA1416 // 已在 OperatingSystem.IsWindows() 分支内调用
        return ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
    }

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectDpapi(byte[] data)
    {
#pragma warning disable CA1416
        return ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
    }

    private byte[] ProtectAes(byte[] data)
    {
        var key = LoadOrCreateAesKey();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(data, 0, data.Length);
        var result = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, result, aes.IV.Length, cipher.Length);
        return result;
    }

    private byte[] UnprotectAes(byte[] data)
    {
        if (data.Length < 17)
            throw new InvalidDataException("凭据表密文过短");

        var key = LoadOrCreateAesKey();
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = data.AsSpan(0, 16).ToArray();
        aes.IV = iv;
        var cipher = data.AsSpan(16).ToArray();
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    private byte[] LoadOrCreateAesKey()
    {
        if (_keyFilePath is null)
            throw new InvalidOperationException("无法确定凭据密钥文件路径");

        if (File.Exists(_keyFilePath))
            return Convert.FromBase64String(File.ReadAllText(_keyFilePath).Trim());

        var key = RandomNumberGenerator.GetBytes(32);
        var dir = Path.GetDirectoryName(_keyFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_keyFilePath, Convert.ToBase64String(key));
        return key;
    }

    private sealed class CredentialFile
    {
        public Dictionary<string, CredentialDto> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CredentialDto
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string AuthKind { get; set; } = SourceFormSchema.AuthNone;
    }
}