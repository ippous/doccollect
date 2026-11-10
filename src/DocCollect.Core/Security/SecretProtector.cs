using System.Security.Cryptography;
using System.Text;

namespace DocCollect.Core.Security;

/// <summary>
/// Chiffre les secrets stockés en base (mots de passe FTP/SFTP, clés Blob, clés d'API IA…).
/// Format : « enc:v1:{keyId}:{base64(nonce|cipher|tag)} ».
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string value);
    bool IsProtected(string? value);
}

public sealed class SecretProtector(IMasterKeyProvider keys) : ISecretProtector
{
    private const string Prefix = "enc:v1:";
    private static readonly byte[] Aad = "doccollect-secret"u8.ToArray();

    public bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public string Protect(string plaintext)
    {
        if (IsProtected(plaintext)) return plaintext;
        var keyId = keys.CurrentKeyId;
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(keys.GetKey(keyId), 16);
        aes.Encrypt(nonce, data, cipher, tag, Aad);
        return $"{Prefix}{keyId}:{Convert.ToBase64String([.. nonce, .. cipher, .. tag])}";
    }

    public string Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        var rest = value[Prefix.Length..];
        var sep = rest.IndexOf(':');
        if (sep <= 0) throw new CryptographicException("Secret protégé mal formé.");
        var keyId = rest[..sep];
        var blob = Convert.FromBase64String(rest[(sep + 1)..]);
        if (blob.Length < 28) throw new CryptographicException("Secret protégé mal formé.");
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(keys.GetKey(keyId), 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, plain.Length), blob.AsSpan(12 + plain.Length, 16), plain, Aad);
        return Encoding.UTF8.GetString(plain);
    }
}

public static class Hashing
{
    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Jeton aléatoire URL-safe (invitations, liens).</summary>
    public static string NewToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
