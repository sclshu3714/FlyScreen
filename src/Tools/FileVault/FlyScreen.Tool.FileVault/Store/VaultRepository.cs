using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlyScreen.Tool.FileVault.Crypto;

namespace FlyScreen.Tool.FileVault.Store;

/// <summary>保险箱落盘仓库：创建/打开/索引/对象读写。</summary>
internal sealed class VaultRepository
{
    public const string ManifestName = "vault.manifest.json";
    public const string KeyWrapName = "keywrap.bin";
    public const string IndexName = "vault.db.enc";
    public const string ObjectsDir = "objects";
    public const string FlyVaultExt = ".flyvault";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private byte[]? _mk;
    private VaultManifestDto? _manifest;
    private VaultIndexDto _index = new();
    private Dictionary<string, VaultEntryDto> _byId = new(StringComparer.Ordinal);

    public string Root { get; private set; } = "";
    public bool IsUnlocked => _mk != null;
    public VaultManifestDto? Manifest => _manifest;
    public IReadOnlyDictionary<string, VaultEntryDto> Entries => _byId;

    public static bool LooksLikeVault(string root)
        => Directory.Exists(root)
           && System.IO.File.Exists(Path.Combine(root, ManifestName))
           && System.IO.File.Exists(Path.Combine(root, KeyWrapName));

    public void Create(string root, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ObjectsDir));

        if (LooksLikeVault(root))
            throw new InvalidOperationException("目标目录已是保险箱");

        var salt = VaultCrypto.GenerateKey(VaultCrypto.SaltSize);
        var iterations = VaultCrypto.DefaultPbkdf2Iterations;
        var kek = VaultCrypto.DeriveKek(password, salt, iterations);
        var mk = VaultCrypto.GenerateKey();
        var wrap = VaultCrypto.WrapKey(kek, mk);
        CryptographicOperations.ZeroMemory(kek);

        var manifest = new VaultManifestDto
        {
            SchemaVersion = 1,
            VaultId = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            Pbkdf2Iterations = iterations,
            SaltBase64 = Convert.ToBase64String(salt),
            ChunkSize = VaultCrypto.DefaultChunkSize
        };

        System.IO.File.WriteAllText(
            Path.Combine(root, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOptions));
        System.IO.File.WriteAllBytes(Path.Combine(root, KeyWrapName), wrap);

        Root = root;
        _manifest = manifest;
        _mk = mk;
        _index = new VaultIndexDto();
        RebuildMap();
        PersistIndexUnlocked();
    }

    public void OpenLocked(string root)
    {
        root = Path.GetFullPath(root);
        if (!LooksLikeVault(root))
            throw new InvalidOperationException("不是有效的保险箱目录");
        var json = System.IO.File.ReadAllText(Path.Combine(root, ManifestName));
        _manifest = JsonSerializer.Deserialize<VaultManifestDto>(json, JsonOptions)
                    ?? throw new InvalidDataException("manifest 损坏");
        Root = root;
        Lock();
    }

    public void Unlock(string password)
    {
        if (_manifest == null || string.IsNullOrEmpty(Root))
            throw new InvalidOperationException("保险箱未打开");
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = Convert.FromBase64String(_manifest.SaltBase64);
        var kek = VaultCrypto.DeriveKek(password, salt, _manifest.Pbkdf2Iterations);
        var wrap = System.IO.File.ReadAllBytes(Path.Combine(Root, KeyWrapName));
        try
        {
            _mk = VaultCrypto.UnwrapKey(kek, wrap);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("密码错误");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        LoadIndexUnlocked();
    }

    public void Lock()
    {
        if (_mk != null)
        {
            CryptographicOperations.ZeroMemory(_mk);
            _mk = null;
        }
        _index = new VaultIndexDto();
        _byId = new Dictionary<string, VaultEntryDto>(StringComparer.Ordinal);
    }

    public IReadOnlyList<VaultEntryView> ListChildren(string? parentId)
    {
        EnsureUnlocked();
        return _index.Entries
            .Where(e => SameParent(e.ParentId, parentId))
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToView)
            .ToList();
    }

    /// <summary>根目录 ParentId 可能是 null 或 ""，导出时必须等价。</summary>
    private static bool SameParent(string? a, string? b)
        => string.IsNullOrEmpty(a)
            ? string.IsNullOrEmpty(b)
            : string.Equals(a, b, StringComparison.Ordinal);

    public VaultEntryView? Get(string id)
    {
        EnsureUnlocked();
        return _byId.TryGetValue(id, out var e) ? ToView(e) : null;
    }

    public string GetVirtualPath(string id)
    {
        EnsureUnlocked();
        var parts = new List<string>();
        var cur = id;
        var guard = 0;
        while (!string.IsNullOrEmpty(cur) && guard++ < 256)
        {
            if (!_byId.TryGetValue(cur, out var e))
                break;
            parts.Add(e.Name);
            cur = e.ParentId ?? "";
            if (string.IsNullOrEmpty(e.ParentId))
                break;
        }
        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    /// <summary>
    /// 解析虚拟路径为文件夹 Id。返回 true 且 folderId=null 表示根；找不到返回 false。
    /// </summary>
    public bool TryResolveVirtualPath(string virtualPath, out string? folderId)
    {
        folderId = null;
        EnsureUnlocked();
        var raw = (virtualPath ?? "").Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(raw) || raw == "/")
            return true;

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? parent = null;
        foreach (var name in parts)
        {
            var hit = _index.Entries.FirstOrDefault(e =>
                e.IsDir
                && string.Equals(e.ParentId, parent, StringComparison.Ordinal)
                && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                folderId = null;
                return false;
            }
            parent = hit.Id;
        }
        folderId = parent;
        return true;
    }

    public VaultEntryDto EnsureDirectory(string? parentId, string name)
    {
        EnsureUnlocked();
        name = SanitizeName(name);
        var existing = _index.Entries.FirstOrDefault(e =>
            e.IsDir
            && string.Equals(e.ParentId, parentId, StringComparison.Ordinal)
            && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var entry = new VaultEntryDto
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentId = parentId,
            Name = name,
            IsDir = true,
            ModifiedUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        _index.Entries.Add(entry);
        _byId[entry.Id] = entry;
        PersistIndexUnlocked();
        return entry;
    }

    public VaultEntryDto CreateUniqueDirectory(string? parentId, string baseName)
    {
        EnsureUnlocked();
        baseName = SanitizeName(baseName);
        return EnsureDirectory(parentId, ResolveUniqueName(parentId, baseName, isDir: true));
    }

    public VaultEntryDto ImportFile(
        string? parentId,
        string sourcePath,
        bool secureDeleteSource,
        IProgress<VaultProgress>? progress,
        CancellationToken ct)
    {
        EnsureUnlocked();
        var name = SanitizeName(Path.GetFileName(sourcePath));
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
            throw new FileNotFoundException(sourcePath);

        var dek = VaultCrypto.GenerateKey();
        var id = Guid.NewGuid().ToString("N");
        var blobPath = Path.Combine(Root, ObjectsDir, id + ".blob");
        var chunkSize = _manifest!.ChunkSize;
        var chunkCount = 0;
        string contentHash;

        using (var src = System.IO.File.OpenRead(sourcePath))
        {
            contentHash = VaultCrypto.Sha256Hex(src);
            using var dst = System.IO.File.Create(blobPath);
            var buf = new byte[chunkSize];
            long done = 0;
            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var enc = VaultCrypto.EncryptChunk(dek, chunkCount, buf.AsSpan(0, read));
                var lenPrefix = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lenPrefix, enc.Length);
                dst.Write(lenPrefix);
                dst.Write(enc);
                chunkCount++;
                done += read;
                progress?.Report(new VaultProgress
                {
                    Phase = "import",
                    CurrentName = name,
                    Done = done,
                    Total = info.Length
                });
            }
        }

        var wrappedDek = VaultCrypto.WrapKey(_mk!, dek);
        CryptographicOperations.ZeroMemory(dek);

        var entry = new VaultEntryDto
        {
            Id = id,
            ParentId = parentId,
            Name = ResolveUniqueName(parentId, name, isDir: false),
            IsDir = false,
            Size = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc.ToString("O"),
            ContentHash = contentHash,
            DekWrappedBase64 = Convert.ToBase64String(wrappedDek),
            ChunkCount = chunkCount
        };
        _index.Entries.Add(entry);
        _byId[entry.Id] = entry;
        UpdateManifestStats();
        PersistIndexUnlocked();

        if (secureDeleteSource)
            SecureDelete.File(sourcePath);

        return entry;
    }

    public void ExportFile(string entryId, string destPath, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        EnsureUnlocked();
        if (!_byId.TryGetValue(entryId, out var entry) || entry.IsDir)
            throw new InvalidOperationException("不是文件条目");

        var dek = VaultCrypto.UnwrapKey(_mk!, Convert.FromBase64String(entry.DekWrappedBase64!));
        var blobPath = Path.Combine(Root, ObjectsDir, entry.Id + ".blob");
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        try
        {
            using var src = System.IO.File.OpenRead(blobPath);
            using var dst = System.IO.File.Create(destPath);
            long done = 0;
            for (var i = 0; i < entry.ChunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var lenBuf = new byte[4];
                if (src.Read(lenBuf, 0, 4) != 4)
                    throw new InvalidDataException("blob 损坏");
                var len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                var enc = new byte[len];
                if (src.Read(enc, 0, len) != len)
                    throw new InvalidDataException("blob 损坏");
                var plain = VaultCrypto.DecryptChunk(dek, i, enc);
                dst.Write(plain);
                done += plain.Length;
                progress?.Report(new VaultProgress
                {
                    Phase = "export",
                    CurrentName = entry.Name,
                    Done = done,
                    Total = entry.Size
                });
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public void ExportTree(string? parentId, string destDir, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        EnsureUnlocked();
        Directory.CreateDirectory(destDir);
        foreach (var child in ListChildren(parentId))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destDir, child.Name);
            if (child.IsDir)
                ExportTree(child.Id, target, progress, ct);
            else
                ExportFile(child.Id, target, progress, ct);
        }
    }

    public void DeleteEntry(string id, bool recursive)
    {
        EnsureUnlocked();
        if (!_byId.TryGetValue(id, out var entry))
            return;

        if (entry.IsDir)
        {
            var children = _index.Entries
                .Where(e => string.Equals(e.ParentId, id, StringComparison.Ordinal))
                .Select(e => e.Id)
                .ToList();
            if (children.Count > 0 && !recursive)
                throw new InvalidOperationException("目录非空");
            foreach (var c in children)
                DeleteEntry(c, recursive: true);
        }
        else
        {
            var blob = Path.Combine(Root, ObjectsDir, entry.Id + ".blob");
            SecureDelete.File(blob);
        }

        _index.Entries.RemoveAll(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        _byId.Remove(id);
        UpdateManifestStats();
        PersistIndexUnlocked();
    }

    public void RenameEntry(string id, string newName)
    {
        EnsureUnlocked();
        if (!_byId.TryGetValue(id, out var entry))
            throw new InvalidOperationException("条目不存在");
        newName = SanitizeName(newName);
        if (string.Equals(entry.Name, newName, StringComparison.OrdinalIgnoreCase))
            return;
        if (NameTaken(entry.ParentId, newName, id))
            throw new InvalidOperationException("名称已存在");
        entry.Name = newName;
        entry.ModifiedUtc = DateTimeOffset.UtcNow.ToString("O");
        PersistIndexUnlocked();
    }

    public void MoveEntry(string id, string? newParentId)
    {
        EnsureUnlocked();
        if (!_byId.TryGetValue(id, out var entry))
            throw new InvalidOperationException("条目不存在");
        if (string.Equals(entry.ParentId, newParentId, StringComparison.Ordinal))
            return;
        if (entry.IsDir && IsUnder(newParentId, id))
            throw new InvalidOperationException("不能移动到自身内部");
        var name = entry.Name;
        entry.ParentId = newParentId;
        if (NameTaken(newParentId, name, entry.Id))
            entry.Name = ResolveUniqueName(newParentId, name, entry.IsDir);
        entry.ModifiedUtc = DateTimeOffset.UtcNow.ToString("O");
        PersistIndexUnlocked();
    }

    public void CopyEntry(string id, string? newParentId)
    {
        EnsureUnlocked();
        if (!_byId.TryGetValue(id, out var entry))
            throw new InvalidOperationException("条目不存在");
        if (entry.IsDir && IsUnder(newParentId, id))
            throw new InvalidOperationException("不能复制到自身内部");
        CopyEntryCore(entry, newParentId);
        UpdateManifestStats();
        PersistIndexUnlocked();
    }

    public void DestroyVault()
    {
        Lock();
        if (string.IsNullOrEmpty(Root) || !Directory.Exists(Root))
            return;
        SecureDelete.DirectoryTree(Root);
        Root = "";
        _manifest = null;
    }

    public void ExportArchive(string flyVaultPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Root) || !Directory.Exists(Root))
            throw new InvalidOperationException("保险箱不存在");
        if (IsUnlocked)
            throw new InvalidOperationException("请先锁定再导出备份包");

        var dest = Path.GetFullPath(flyVaultPath);
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);
        if (System.IO.File.Exists(dest))
            System.IO.File.Delete(dest);

        ZipFile.CreateFromDirectory(Root, dest, CompressionLevel.Optimal, includeBaseDirectory: false);
        ct.ThrowIfCancellationRequested();
    }

    public static void ImportArchive(string flyVaultPath, string destRoot)
    {
        destRoot = Path.GetFullPath(destRoot);
        if (Directory.Exists(destRoot) && Directory.EnumerateFileSystemEntries(destRoot).Any())
            throw new InvalidOperationException("目标目录非空");
        Directory.CreateDirectory(destRoot);
        ZipFile.ExtractToDirectory(flyVaultPath, destRoot);
        if (!LooksLikeVault(destRoot))
        {
            SecureDelete.DirectoryTree(destRoot);
            throw new InvalidDataException("备份包不是有效保险箱");
        }
    }

    public void RelocateRoot(string newRoot)
    {
        newRoot = Path.GetFullPath(newRoot);
        Root = newRoot;
    }

    public IEnumerable<VaultEntryDto> EnumerateFiles()
    {
        EnsureUnlocked();
        return _index.Entries.Where(e => !e.IsDir);
    }

    private void LoadIndexUnlocked()
    {
        EnsureUnlocked();
        var path = Path.Combine(Root, IndexName);
        if (!System.IO.File.Exists(path))
        {
            _index = new VaultIndexDto();
            RebuildMap();
            return;
        }

        var enc = System.IO.File.ReadAllBytes(path);
        var plain = VaultCrypto.AesGcmDecrypt(_mk!, enc);
        _index = JsonSerializer.Deserialize<VaultIndexDto>(plain, JsonOptions) ?? new VaultIndexDto();
        CryptographicOperations.ZeroMemory(plain);
        RebuildMap();
    }

    private void PersistIndexUnlocked()
    {
        EnsureUnlocked();
        var plain = JsonSerializer.SerializeToUtf8Bytes(_index, JsonOptions);
        var enc = VaultCrypto.AesGcmEncrypt(_mk!, plain);
        CryptographicOperations.ZeroMemory(plain);
        var path = Path.Combine(Root, IndexName);
        var tmp = path + ".tmp";
        System.IO.File.WriteAllBytes(tmp, enc);
        System.IO.File.Move(tmp, path, overwrite: true);
        PersistManifest();
    }

    private void PersistManifest()
    {
        if (_manifest == null || string.IsNullOrEmpty(Root)) return;
        System.IO.File.WriteAllText(
            Path.Combine(Root, ManifestName),
            JsonSerializer.Serialize(_manifest, JsonOptions));
    }

    private void UpdateManifestStats()
    {
        if (_manifest == null) return;
        _manifest.EntryCount = _index.Entries.Count;
        _manifest.TotalBytes = _index.Entries.Where(e => !e.IsDir).Sum(e => e.Size);
    }

    private void RebuildMap()
    {
        _byId = _index.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    private void EnsureUnlocked()
    {
        if (_mk == null || _manifest == null)
            throw new InvalidOperationException("保险箱未解锁");
    }

    private void CopyEntryCore(VaultEntryDto src, string? parentId)
    {
        var id = Guid.NewGuid().ToString("N");
        if (!src.IsDir)
        {
            var srcBlob = Path.Combine(Root, ObjectsDir, src.Id + ".blob");
            var dstBlob = Path.Combine(Root, ObjectsDir, id + ".blob");
            if (File.Exists(srcBlob))
                File.Copy(srcBlob, dstBlob, overwrite: false);
        }

        var copy = new VaultEntryDto
        {
            Id = id,
            ParentId = parentId,
            Name = ResolveUniqueName(parentId, src.Name, src.IsDir),
            IsDir = src.IsDir,
            Size = src.Size,
            ModifiedUtc = DateTimeOffset.UtcNow.ToString("O"),
            ContentHash = src.ContentHash,
            DekWrappedBase64 = src.DekWrappedBase64,
            ChunkCount = src.ChunkCount
        };
        _index.Entries.Add(copy);
        _byId[copy.Id] = copy;
        if (!src.IsDir)
            return;
        foreach (var child in _index.Entries.Where(e => string.Equals(e.ParentId, src.Id, StringComparison.Ordinal)).ToList())
            CopyEntryCore(child, id);
    }

    private bool NameTaken(string? parentId, string name, string exceptId) =>
        _index.Entries.Any(e =>
            !string.Equals(e.Id, exceptId, StringComparison.Ordinal)
            && string.Equals(e.ParentId, parentId, StringComparison.Ordinal)
            && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool IsUnder(string? nodeId, string ancestorId)
    {
        var cur = nodeId;
        while (!string.IsNullOrEmpty(cur))
        {
            if (string.Equals(cur, ancestorId, StringComparison.Ordinal))
                return true;
            cur = _byId.TryGetValue(cur, out var e) ? e.ParentId : null;
        }
        return false;
    }

    private string ResolveUniqueName(string? parentId, string name, bool isDir)
    {
        var ext = isDir ? "" : Path.GetExtension(name);
        var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        var i = 1;
        while (_index.Entries.Any(e =>
                   string.Equals(e.ParentId, parentId, StringComparison.Ordinal)
                   && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = isDir ? $"{stem} ({i})" : $"{stem} ({i}){ext}";
            i++;
            if (i > 10_000) throw new InvalidOperationException("无法生成唯一文件名");
        }
        return name;
    }

    private static string SanitizeName(string name)
    {
        name = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            throw new ArgumentException("非法名称");
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static VaultEntryView ToView(VaultEntryDto e) => new()
    {
        Id = e.Id,
        ParentId = e.ParentId,
        Name = e.Name,
        IsDir = e.IsDir,
        Size = e.Size,
        ModifiedUtc = DateTimeOffset.TryParse(e.ModifiedUtc, out var dt) ? dt : DateTimeOffset.UnixEpoch
    };
}
