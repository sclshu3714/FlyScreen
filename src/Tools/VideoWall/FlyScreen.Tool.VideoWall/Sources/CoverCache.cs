using System.Security.Cryptography;
using System.Text;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>
/// 首帧封面缓存：<c>&lt;工具数据目录&gt;/covers/&lt;key&gt;.jpg</c>。
/// 只负责路径与清理，抓帧本身走 <see cref="Contracts.IMediaBackend"/>。
/// </summary>
public sealed class CoverCache
{
    public const string FileExtension = ".jpg";

    public CoverCache(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("directory 不能为空", nameof(directory));

        Directory = Path.GetFullPath(directory);
    }

    public string Directory { get; }

    /// <summary>
    /// 封面键 = SHA256(protocol|address|transport|bufferMs|credentialRef) 前 16 位十六进制。
    /// 配置变更即换键，旧封面自然失效（由 <see cref="RemoveOrphans"/> 清理）。
    /// </summary>
    public static string BuildKey(VideoEndpoint endpoint)
    {
        var material = string.Join('|',
            (endpoint.Protocol ?? "").Trim().ToLowerInvariant(),
            (endpoint.Address ?? "").Trim(),
            (endpoint.Transport ?? "").Trim().ToLowerInvariant(),
            endpoint.BufferMs?.ToString() ?? "",
            endpoint.CredentialRef ?? "");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public string PathFor(VideoEndpoint endpoint) => PathForKey(BuildKey(endpoint));

    /// <summary>按键取路径；键必须是 16 位十六进制，避免路径穿越。</summary>
    public string PathForKey(string key)
    {
        if (!IsValidKey(key))
            throw new ArgumentException($"非法封面键：{key}", nameof(key));

        return Path.Combine(Directory, key + FileExtension);
    }

    public bool Exists(string? key) => IsValidKey(key) && File.Exists(PathForKey(key!));

    public void Delete(string? key)
    {
        if (!IsValidKey(key)) return;
        var path = PathForKey(key!);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>清理不再被任何源引用的封面文件。</summary>
    public int RemoveOrphans(IEnumerable<string?> referencedKeys)
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;

        var keep = referencedKeys
            .Where(k => IsValidKey(k))
            .Select(k => k!.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var removed = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*" + FileExtension))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (keep.Contains(name)) continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
                // 被占用（可能正在读取）→ 下次再清
            }
        }

        return removed;
    }

    private static bool IsValidKey(string? key)
        => key is { Length: 16 } && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
