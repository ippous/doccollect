using System.Security.Cryptography;
using DocCollect.Core.Ai;
using DocCollect.Core.Security;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Shared;

namespace DocCollect.Core.Documents;

public sealed record IngestRequest(
    Tenant Tenant,
    Dossier Dossier,
    Guid ItemId,
    string FileName,
    string DeclaredContentType,
    Stream Content,
    string? UserId);

public sealed record IngestResult(StoredDocument Document, ClassificationResult? Classification);

/// <summary>
/// Chaîne de dépôt d'un fichier :
/// copie temporaire + SHA-256 + limite de taille → détection du type réel → contrôles métier
/// → vérification IA (option payante) → chiffrement (option) → écriture dans le stockage → rattachement au dossier.
/// La persistance (SaveChanges) reste à la charge de l'appelant.
/// </summary>
public sealed class DocumentIngestionService(
    IStorageResolver storageResolver,
    IDocumentEncryptor encryptor,
    AiClassifierRegistry aiRegistry,
    AiOptions aiOptions,
    TimeProvider clock)
{
    public async Task<IngestResult> IngestAsync(IngestRequest r, CancellationToken ct = default)
    {
        var item = r.Dossier.GetItem(r.ItemId);
        var maxBytes = (long)item.MaxSizeMb * 1024 * 1024;
        var fileName = SanitizeFileName(r.FileName);
        var now = clock.GetUtcNow();

        await using var plain = TempFile();
        var (size, sha) = await CopyWithHashAsync(r.Content, plain, maxBytes, ct);
        plain.Position = 0;

        var header = new byte[ContentSniffer.HeaderLength];
        var read = await plain.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var contentType = ContentSniffer.Resolve(header.AsSpan(0, read), r.DeclaredContentType, fileName);
        if (ContentSniffer.IsDangerous(contentType))
            throw new DomainException(ErrorCodes.ContentTypeNotAllowed, "Les fichiers exécutables sont refusés.");

        // Contrôles métier avec la taille et le type réels (avant toute écriture dans le stockage).
        r.Dossier.EnsureCanUpload(r.ItemId, contentType, size);

        var document = new StoredDocument
        {
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = size,
            Sha256 = sha,
            UploadedAt = now,
            UploadedByUserId = r.UserId
        };

        // Vérification IA : ne bloque jamais le dépôt, le verdict est informatif.
        ClassificationResult? classification = null;
        var classifier = r.Tenant.AiVerificationEnabled ? aiRegistry.Find(r.Tenant.AiProviderKey) : null;
        if (classifier is not null && !string.IsNullOrWhiteSpace(item.ExpectedDocumentType)
            && classifier.SupportsContentType(contentType) && size <= (long)aiOptions.MaxDocumentSizeMb * 1024 * 1024)
        {
            plain.Position = 0;
            var bytes = new byte[size];
            await plain.ReadExactlyAsync(bytes, ct);
            classification = await classifier.ClassifyAsync(
                new ClassificationRequest(item.ExpectedDocumentType!, item.Label, fileName, contentType, bytes), ct);
            document.AiVerdict = classification.Verdict;
            document.AiDetectedType = classification.DetectedType;
            document.AiConfidence = classification.Confidence;
            document.AiComment = classification.Comment;
            document.AiProviderKey = classifier.Key;
        }

        var resolved = await storageResolver.ResolveForTenantAsync(r.Tenant, ct);
        document.StorageProfileId = resolved.ProfileId;
        document.StorageKey = StorageKeys.ForDocument(r.Tenant.Slug, r.Dossier.Id, document.Id, now);

        plain.Position = 0;
        if (r.Tenant.EncryptAtRest)
        {
            await using var encrypted = TempFile();
            var wrapped = await encryptor.EncryptAsync(document.Id, plain, encrypted, ct);
            encrypted.Position = 0;
            await resolved.Storage.WriteAsync(document.StorageKey, encrypted, ct);
            document.Encrypted = true;
            document.MasterKeyId = wrapped.KeyId;
            document.WrappedKey = wrapped.WrappedKeyBase64;
        }
        else
        {
            await resolved.Storage.WriteAsync(document.StorageKey, plain, ct);
        }

        try
        {
            r.Dossier.AddDocument(r.ItemId, document, now);
        }
        catch
        {
            // Cohérence : on ne laisse pas d'objet orphelin dans le stockage.
            await resolved.Storage.DeleteAsync(document.StorageKey, CancellationToken.None);
            throw;
        }
        return new IngestResult(document, classification);
    }

    /// <summary>Écrit le contenu en clair du document dans <paramref name="output"/> (déchiffrement et contrôle d'intégrité).</summary>
    public async Task OpenAsync(StoredDocument document, Stream output, CancellationToken ct = default)
    {
        var storage = await storageResolver.ResolveAsync(document.StorageProfileId, ct);
        await using var source = await storage.OpenReadAsync(document.StorageKey, ct);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var hashing = new HashingWriteStream(output, sha);
        if (document.Encrypted)
        {
            if (document.WrappedKey is null || document.MasterKeyId is null)
                throw new CryptographicException("Clé de document manquante.");
            await encryptor.DecryptAsync(document.Id, new WrappedKey(document.MasterKeyId, document.WrappedKey), source, hashing, ct);
        }
        else
        {
            await source.CopyToAsync(hashing, ct);
        }
        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (!string.Equals(actual, document.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Empreinte du document incohérente : fichier altéré dans le stockage.");
    }

    public async Task DeleteAsync(StoredDocument document, CancellationToken ct = default)
    {
        var storage = await storageResolver.ResolveAsync(document.StorageProfileId, ct);
        await storage.DeleteAsync(document.StorageKey, ct);
    }

    internal static string SanitizeFileName(string name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(n.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().Trim('.');
        if (clean.Length == 0) clean = "document";
        return clean.Length > 200 ? clean[..200] : clean;
    }

    private static FileStream TempFile() =>
        new(Path.Combine(Path.GetTempPath(), "doccollect-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    private static async Task<(long Size, string Sha256)> CopyWithHashAsync(Stream input, Stream output, long maxBytes, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            if (total > maxBytes)
                throw new DomainException(ErrorCodes.FileTooLarge, $"Fichier trop volumineux (max {maxBytes / 1024 / 1024} Mo).");
            sha.AppendData(buffer, 0, n);
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        await output.FlushAsync(ct);
        return (total, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    /// <summary>Flux d'écriture qui calcule une empreinte au passage.</summary>
    private sealed class HashingWriteStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, ct);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        // Le flux de sortie appartient à l'appelant : on ne le ferme pas.
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
