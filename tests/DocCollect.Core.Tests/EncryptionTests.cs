using System.Security.Cryptography;
using DocCollect.Core.Security;

namespace DocCollect.Core.Tests;

public class EncryptionTests
{
    private const int Chunk = 1024;

    private static async Task<(byte[] Cipher, WrappedKey Key)> Encrypt(DocumentEncryptor enc, Guid id, byte[] plain)
    {
        using var output = new MemoryStream();
        var key = await enc.EncryptAsync(id, new MemoryStream(plain), output);
        return (output.ToArray(), key);
    }

    private static async Task<byte[]> Decrypt(DocumentEncryptor enc, Guid id, WrappedKey key, byte[] cipher)
    {
        using var output = new MemoryStream();
        await enc.DecryptAsync(id, key, new MemoryStream(cipher), output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk + 5)]
    [InlineData(4 * Chunk)]
    public async Task Roundtrip_preserves_content(int size)
    {
        var enc = new DocumentEncryptor(TestData.Keys(), Chunk);
        var id = Guid.NewGuid();
        var plain = RandomNumberGenerator.GetBytes(size);
        var (cipher, key) = await Encrypt(enc, id, plain);
        Assert.NotEqual(plain.Length, cipher.Length);
        Assert.Equal(plain, await Decrypt(enc, id, key, cipher));
    }

    [Fact]
    public async Task Modified_byte_is_detected()
    {
        var enc = new DocumentEncryptor(TestData.Keys(), Chunk);
        var id = Guid.NewGuid();
        var (cipher, key) = await Encrypt(enc, id, RandomNumberGenerator.GetBytes(3000));
        cipher[100] ^= 0x01;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(enc, id, key, cipher));
    }

    [Fact]
    public async Task Truncation_at_a_chunk_boundary_is_detected()
    {
        var enc = new DocumentEncryptor(TestData.Keys(), Chunk);
        var id = Guid.NewGuid();
        var (cipher, key) = await Encrypt(enc, id, RandomNumberGenerator.GetBytes(3 * Chunk));
        // En-tête (20) + 2 blocs complets (4 + 1024 + 16 chacun) : on retire le dernier bloc.
        var truncated = cipher.AsSpan(0, 20 + 2 * (4 + Chunk + 16)).ToArray();
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(enc, id, key, truncated));
    }

    [Fact]
    public async Task Appended_data_is_detected()
    {
        var enc = new DocumentEncryptor(TestData.Keys(), Chunk);
        var id = Guid.NewGuid();
        var (cipher, key) = await Encrypt(enc, id, RandomNumberGenerator.GetBytes(500));
        byte[] extended = [.. cipher, 1, 2, 3];
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(enc, id, key, extended));
    }

    [Fact]
    public async Task Key_bound_to_document_id()
    {
        var enc = new DocumentEncryptor(TestData.Keys(), Chunk);
        var (cipher, key) = await Encrypt(enc, Guid.NewGuid(), RandomNumberGenerator.GetBytes(100));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(enc, Guid.NewGuid(), key, cipher));
    }

    [Fact]
    public async Task Old_master_key_remains_readable_after_rotation()
    {
        var id = Guid.NewGuid();
        var plain = RandomNumberGenerator.GetBytes(2500);
        var before = new DocumentEncryptor(TestData.Keys("k1"), Chunk);
        var (cipher, key) = await Encrypt(before, id, plain);
        Assert.Equal("k1", key.KeyId);

        var after = new DocumentEncryptor(TestData.Keys("k2", "k1"), Chunk);
        Assert.Equal(plain, await Decrypt(after, id, key, cipher));
        var (_, newKey) = await Encrypt(after, Guid.NewGuid(), plain);
        Assert.Equal("k2", newKey.KeyId);
    }

    [Fact]
    public void Master_key_configuration_is_validated()
    {
        Assert.Throws<InvalidOperationException>(() => new ConfiguredMasterKeyProvider(new EncryptionOptions
        {
            CurrentKeyId = "k1", Keys = { ["k1"] = Convert.ToBase64String(new byte[16]) }
        }));
        Assert.Throws<InvalidOperationException>(() => new ConfiguredMasterKeyProvider(new EncryptionOptions
        {
            CurrentKeyId = "absente", Keys = { ["k1"] = ConfiguredMasterKeyProvider.GenerateKey() }
        }));
    }

    [Fact]
    public void Secret_protector_roundtrip_and_idempotence()
    {
        var p = new SecretProtector(TestData.Keys());
        var protectedValue = p.Protect("motdepasse-ftp");
        Assert.True(p.IsProtected(protectedValue));
        Assert.DoesNotContain("motdepasse", protectedValue);
        Assert.Equal(protectedValue, p.Protect(protectedValue));
        Assert.Equal("motdepasse-ftp", p.Unprotect(protectedValue));
        Assert.Equal("en clair", p.Unprotect("en clair"));
    }

    [Fact]
    public void Tokens_are_url_safe_and_hashes_stable()
    {
        var t = Hashing.NewToken();
        Assert.DoesNotContain("+", t);
        Assert.DoesNotContain("/", t);
        Assert.Equal(Hashing.Sha256Hex("abc"), Hashing.Sha256Hex("abc"));
        Assert.Equal(64, Hashing.Sha256Hex("abc").Length);
    }
}
