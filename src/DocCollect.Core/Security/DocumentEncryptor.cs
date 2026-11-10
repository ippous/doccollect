using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DocCollect.Core.Security;

public sealed record WrappedKey(string KeyId, string WrappedKeyBase64);

public interface IDocumentEncryptor
{
    /// <summary>Chiffre <paramref name="plaintext"/> vers <paramref name="output"/> et renvoie la clé de données scellée.</summary>
    Task<WrappedKey> EncryptAsync(Guid documentId, Stream plaintext, Stream output, CancellationToken ct = default);
    Task DecryptAsync(Guid documentId, WrappedKey key, Stream ciphertext, Stream output, CancellationToken ct = default);
}

/// <summary>
/// Chiffrement par enveloppe :
/// - une clé de données (DEK, AES-256) aléatoire par document ;
/// - la DEK est scellée par la clé maître (AES-GCM, données associées = identifiant du document,
///   ce qui empêche d'échanger les clés entre documents) ;
/// - le contenu est chiffré en blocs AES-GCM (format « DCE1 ») : chaque bloc a son nonce dérivé
///   d'un compteur et un indicateur « dernier bloc » authentifié, ce qui détecte toute troncature,
///   réordonnancement ou modification.
/// </summary>
public sealed class DocumentEncryptor(IMasterKeyProvider keys, int chunkSize = 64 * 1024) : IDocumentEncryptor
{
    private static readonly byte[] Magic = "DCE1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxChunkSize = 16 * 1024 * 1024;

    // ---------- Scellement de la DEK ----------

    internal byte[] WrapKey(Guid documentId, byte[] dek, string keyId)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[dek.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(keys.GetKey(keyId), TagSize);
        aes.Encrypt(nonce, dek, cipher, tag, KeyAad(documentId, keyId));
        return [.. nonce, .. cipher, .. tag];
    }

    internal byte[] UnwrapKey(Guid documentId, WrappedKey wrapped)
    {
        var blob = Convert.FromBase64String(wrapped.WrappedKeyBase64);
        if (blob.Length != NonceSize + 32 + TagSize) throw new CryptographicException("Clé scellée invalide.");
        var dek = new byte[32];
        using var aes = new AesGcm(keys.GetKey(wrapped.KeyId), TagSize);
        aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize, 32), blob.AsSpan(NonceSize + 32, TagSize), dek,
            KeyAad(documentId, wrapped.KeyId));
        return dek;
    }

    private static byte[] KeyAad(Guid documentId, string keyId) => [.. documentId.ToByteArray(), .. Encoding.UTF8.GetBytes(keyId)];

    // ---------- Contenu ----------

    public async Task<WrappedKey> EncryptAsync(Guid documentId, Stream plaintext, Stream output, CancellationToken ct = default)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var baseNonce = RandomNumberGenerator.GetBytes(NonceSize);
            var header = new byte[Magic.Length + 4 + NonceSize];
            Magic.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), chunkSize);
            baseNonce.CopyTo(header, 8);
            await output.WriteAsync(header, ct);

            using var aes = new AesGcm(dek, TagSize);
            var current = new byte[chunkSize];
            var next = new byte[chunkSize];
            var cipher = new byte[chunkSize];
            var tag = new byte[TagSize];
            var lenBuf = new byte[4];

            // Lecture avec un bloc d'avance pour savoir lequel est le dernier.
            var currentLen = await ReadFullAsync(plaintext, current, ct);
            long counter = 0;
            while (true)
            {
                var nextLen = currentLen == chunkSize ? await ReadFullAsync(plaintext, next, ct) : 0;
                var isFinal = nextLen == 0;

                aes.Encrypt(ChunkNonce(baseNonce, counter), current.AsSpan(0, currentLen), cipher.AsSpan(0, currentLen), tag,
                    ChunkAad(counter, isFinal));
                BinaryPrimitives.WriteInt32BigEndian(lenBuf, currentLen);
                await output.WriteAsync(lenBuf, ct);
                await output.WriteAsync(cipher.AsMemory(0, currentLen), ct);
                await output.WriteAsync(tag, ct);

                if (isFinal) break;
                (current, next) = (next, current);
                currentLen = nextLen;
                counter++;
            }
            await output.FlushAsync(ct);

            var keyId = keys.CurrentKeyId;
            return new WrappedKey(keyId, Convert.ToBase64String(WrapKey(documentId, dek, keyId)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task DecryptAsync(Guid documentId, WrappedKey key, Stream ciphertext, Stream output, CancellationToken ct = default)
    {
        var dek = UnwrapKey(documentId, key);
        try
        {
            var header = new byte[Magic.Length + 4 + NonceSize];
            if (await ReadFullAsync(ciphertext, header, ct) != header.Length || !header.AsSpan(0, 4).SequenceEqual(Magic))
                throw new CryptographicException("En-tête de fichier chiffré invalide.");
            var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            if (size <= 0 || size > MaxChunkSize) throw new CryptographicException("Taille de bloc invalide.");
            var baseNonce = header.AsSpan(8, NonceSize).ToArray();

            using var aes = new AesGcm(dek, TagSize);
            var cipher = new byte[size];
            var plain = new byte[size];
            var tag = new byte[TagSize];
            var lenBuf = new byte[4];
            long counter = 0;

            while (true)
            {
                if (await ReadFullAsync(ciphertext, lenBuf, ct) != 4)
                    throw new CryptographicException("Fichier chiffré tronqué.");
                var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                if (len < 0 || len > size) throw new CryptographicException("Longueur de bloc invalide.");
                if (await ReadFullAsync(ciphertext, cipher.AsMemory(0, len), ct) != len
                    || await ReadFullAsync(ciphertext, tag, ct) != TagSize)
                    throw new CryptographicException("Fichier chiffré tronqué.");

                // Un bloc plus court que la taille nominale est forcément le dernier ; un bloc plein peut l'être aussi.
                var nonce = ChunkNonce(baseNonce, counter);
                bool isFinal;
                try
                {
                    aes.Decrypt(nonce, cipher.AsSpan(0, len), tag, plain.AsSpan(0, len), ChunkAad(counter, true));
                    isFinal = true;
                }
                catch (CryptographicException) when (len == size)
                {
                    aes.Decrypt(nonce, cipher.AsSpan(0, len), tag, plain.AsSpan(0, len), ChunkAad(counter, false));
                    isFinal = false;
                }

                await output.WriteAsync(plain.AsMemory(0, len), ct);
                if (isFinal)
                {
                    if (ciphertext.ReadByte() != -1) throw new CryptographicException("Données inattendues après le dernier bloc.");
                    break;
                }
                counter++;
            }
            await output.FlushAsync(ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static byte[] ChunkNonce(byte[] baseNonce, long counter)
    {
        var nonce = (byte[])baseNonce.Clone();
        Span<byte> c = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(c, counter);
        for (var i = 0; i < 8; i++) nonce[NonceSize - 8 + i] ^= c[i];
        return nonce;
    }

    private static byte[] ChunkAad(long counter, bool isFinal)
    {
        var aad = new byte[9];
        BinaryPrimitives.WriteInt64BigEndian(aad, counter);
        aad[8] = isFinal ? (byte)1 : (byte)0;
        return aad;
    }

    private static Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct) => ReadFullAsync(s, buffer.AsMemory(), ct);

    private static async Task<int> ReadFullAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[total..], ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
