using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FlyScreen.Tool.FileVault.Crypto;

/// <summary>保险箱密码学原语：PBKDF2 + AES-256-GCM。</summary>
internal static class VaultCrypto
{
    public const int MasterKeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int SaltSize = 16;
    public const int DefaultChunkSize = 1 * 1024 * 1024;
    public const int DefaultPbkdf2Iterations = 600_000;

    public static byte[] GenerateKey(int size = MasterKeySize)
    {
        var key = new byte[size];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static byte[] DeriveKek(string password, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(salt);
        if (iterations < 100_000)
            throw new ArgumentOutOfRangeException(nameof(iterations));

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            MasterKeySize);
    }

    public static byte[] AesGcmEncrypt(byte[] key, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> aad = default)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plain, cipher, tag, aad);

        var result = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, result, NonceSize + TagSize, cipher.Length);
        return result;
    }

    public static byte[] AesGcmDecrypt(byte[] key, ReadOnlySpan<byte> package, ReadOnlySpan<byte> aad = default)
    {
        if (package.Length < NonceSize + TagSize)
            throw new CryptographicException("密文过短");

        var nonce = package[..NonceSize];
        var tag = package.Slice(NonceSize, TagSize);
        var cipher = package[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain, aad);
        return plain;
    }

    /// <summary>分块加密：每块独立 nonce；前 4 字节为块序号（大端）写入 AAD。</summary>
    public static byte[] EncryptChunk(byte[] dek, int chunkIndex, ReadOnlySpan<byte> plain)
    {
        Span<byte> aad = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(aad, chunkIndex);
        return AesGcmEncrypt(dek, plain, aad);
    }

    public static byte[] DecryptChunk(byte[] dek, int chunkIndex, ReadOnlySpan<byte> package)
    {
        Span<byte> aad = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(aad, chunkIndex);
        return AesGcmDecrypt(dek, package, aad);
    }

    public static byte[] WrapKey(byte[] wrappingKey, byte[] payload)
        => AesGcmEncrypt(wrappingKey, payload);

    public static byte[] UnwrapKey(byte[] wrappingKey, byte[] package)
        => AesGcmDecrypt(wrappingKey, package);

    public static string Sha256Hex(Stream stream)
    {
        stream.Position = 0;
        var hash = SHA256.HashData(stream);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
