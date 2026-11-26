using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyScreen.Services;

/// <summary>
/// 布局电脑解锁密码本机库（不参与 Layout/Behavior 同步）。
/// 全平台 AES 密钥文件加密；落盘 <c>machine-unlock.credentials.bin</c>。
/// </summary>
public sealed class MachineUnlockCredentialStore
{
    private const string FileName = "machine-unlock.credentials.bin";
    private const string KeyFileName = "machine-unlock.credentials.key";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object Gate = new();
    private static MachineUnlockCredentialStore? _shared;

    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _filePath;
    private readonly string _keyFilePath;

    public MachineUnlockCredentialStore(string? directory = null)
    {
        var dir = string.IsNullOrWhiteSpace(directory)
            ? FlyScreenDataPaths.Root
            : Path.GetFullPath(directory);
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, FileName);
        _keyFilePath = Path.Combine(dir, KeyFileName);
    }

    /// <summary>进程内单例（指向默认 AppData）。</summary>
    public static MachineUnlockCredentialStore Shared
    {
        get
        {
            lock (Gate)
            {
                if (_shared != null) return _shared;
                _shared = new MachineUnlockCredentialStore();
                try { _shared.Load(); } catch { /* ignore corrupt */ }
                return _shared;
            }
        }
    }

    public void Load()
    {
        _passwords.Clear();
        if (!File.Exists(_filePath)) return;

        var encrypted = File.ReadAllBytes(_filePath);
        if (encrypted.Length == 0) return;

        var plain = Unprotect(encrypted);
        var file = JsonSerializer.Deserialize<CredentialFile>(plain, JsonOptions);
        if (file?.Entries == null) return;

        foreach (var (name, password) in file.Entries)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password)) continue;
            _passwords[name.Trim()] = password;
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new CredentialFile
        {
            Entries = _passwords.ToDictionary(
                kv => kv.Key,
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase)
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
        var encrypted = Protect(json);
        var temp = _filePath + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, _filePath, overwrite: true);
    }

    public string? Get(string machineName)
    {
        if (string.IsNullOrWhiteSpace(machineName)) return null;
        return _passwords.TryGetValue(machineName.Trim(), out var p) ? p : null;
    }

    public bool Has(string machineName)
        => !string.IsNullOrEmpty(Get(machineName));

    public void Set(string machineName, string password)
    {
        if (string.IsNullOrWhiteSpace(machineName)) return;
        var name = machineName.Trim();
        if (string.IsNullOrEmpty(password))
            _passwords.Remove(name);
        else
            _passwords[name] = password;
    }

    public void SetMany(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var (name, password) in entries)
            Set(name, password);
    }

    private byte[] Protect(byte[] data)
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

    private byte[] Unprotect(byte[] data)
    {
        if (data.Length < 17)
            throw new InvalidDataException("解锁凭据密文过短");

        var key = LoadOrCreateAesKey();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = data.AsSpan(0, 16).ToArray();
        var cipher = data.AsSpan(16).ToArray();
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    private byte[] LoadOrCreateAesKey()
    {
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
        public Dictionary<string, string> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
