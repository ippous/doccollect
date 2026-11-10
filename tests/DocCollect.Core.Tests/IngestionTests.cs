using System.Security.Cryptography;
using DocCollect.Core.Ai;
using DocCollect.Core.Documents;
using DocCollect.Core.Security;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Shared;

namespace DocCollect.Core.Tests;

public class IngestionTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly LocalFileSystemStorage _storage;
    private readonly FixedStorageResolver _resolver;

    public IngestionTests()
    {
        _storage = new LocalFileSystemStorage(_dir.Path);
        _resolver = new FixedStorageResolver(_storage);
    }

    public void Dispose() => _dir.Dispose();

    private DocumentIngestionService Service(params IDocumentClassifier[] classifiers) =>
        new(_resolver, new DocumentEncryptor(TestData.Keys(), 4096),
            new AiClassifierRegistry(new AiOptions(), _ => new HttpClient(), classifiers),
            new AiOptions(), TimeProvider.System);

    [Fact]
    public async Task Encrypted_upload_is_stored_encrypted_and_reads_back()
    {
        var tenant = TestData.Tenant(encrypt: true);
        var dossier = TestData.OpenDossier();
        var pdf = TestData.Pdf(10_000);
        var svc = Service();

        var result = await svc.IngestAsync(new IngestRequest(tenant, dossier, dossier.Items[1].Id, "domicile.pdf", "application/pdf", new MemoryStream(pdf), "u1"));
        var doc = result.Document;

        Assert.True(doc.Encrypted);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pdf)), doc.Sha256);
        Assert.Equal(_resolver.ProfileId, doc.StorageProfileId);
        Assert.Equal(ItemStatus.Accepted, dossier.Items[1].Status);

        var raw = await File.ReadAllBytesAsync(Path.Combine(_dir.Path, doc.StorageKey));
        Assert.NotEqual(pdf.Length, raw.Length);
        Assert.Equal("DCE1"u8.ToArray(), raw[..4]);

        using var output = new MemoryStream();
        await svc.OpenAsync(doc, output);
        Assert.Equal(pdf, output.ToArray());
    }

    [Fact]
    public async Task Plain_upload_when_encryption_disabled()
    {
        var dossier = TestData.OpenDossier();
        var pdf = TestData.Pdf();
        var result = await Service().IngestAsync(new IngestRequest(TestData.Tenant(encrypt: false), dossier, dossier.Items[1].Id, "d.pdf", "application/pdf", new MemoryStream(pdf), null));
        Assert.False(result.Document.Encrypted);
        Assert.Equal(pdf, await File.ReadAllBytesAsync(Path.Combine(_dir.Path, result.Document.StorageKey)));
    }

    [Fact]
    public async Task Tampered_stored_file_is_detected_on_read()
    {
        var dossier = TestData.OpenDossier();
        var svc = Service();
        var result = await svc.IngestAsync(new IngestRequest(TestData.Tenant(encrypt: false), dossier, dossier.Items[1].Id, "d.pdf", "application/pdf", new MemoryStream(TestData.Pdf()), null));
        var path = Path.Combine(_dir.Path, result.Document.StorageKey);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => svc.OpenAsync(result.Document, new MemoryStream()));
    }

    [Fact]
    public async Task Real_content_type_wins_over_declared_one()
    {
        var dossier = TestData.OpenDossier();
        // Déclaré PDF mais c'est un JPEG : refusé sur « Justificatif de domicile » (PDF uniquement).
        var ex = await Assert.ThrowsAsync<DomainException>(() => Service().IngestAsync(
            new IngestRequest(TestData.Tenant(), dossier, dossier.Items[1].Id, "faux.pdf", "application/pdf", new MemoryStream(TestData.Jpeg()), null)));
        Assert.Equal(ErrorCodes.ContentTypeNotAllowed, ex.Code);
        Assert.Empty(Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Executables_are_refused()
    {
        var dossier = TestData.OpenDossier();
        var exe = new byte[100];
        exe[0] = (byte)'M'; exe[1] = (byte)'Z';
        await Assert.ThrowsAsync<DomainException>(() => Service().IngestAsync(
            new IngestRequest(TestData.Tenant(), dossier, dossier.Items[2].Id, "photo.jpg", "image/jpeg", new MemoryStream(exe), null)));
    }

    [Fact]
    public async Task Size_limit_is_enforced_while_streaming()
    {
        var dossier = TestData.OpenDossier();
        var big = TestData.Pdf(6 * 1024 * 1024);
        var ex = await Assert.ThrowsAsync<DomainException>(() => Service().IngestAsync(
            new IngestRequest(TestData.Tenant(), dossier, dossier.Items[0].Id, "id.pdf", "application/pdf", new MemoryStream(big), null)));
        Assert.Equal(ErrorCodes.FileTooLarge, ex.Code);
    }

    [Fact]
    public async Task Ai_check_runs_only_when_enabled_for_tenant()
    {
        var fake = new FakeClassifier("fake", AiVerdict.Mismatch);
        var svc = Service(fake);

        var d1 = TestData.OpenDossier();
        var r1 = await svc.IngestAsync(new IngestRequest(TestData.Tenant(ai: false, aiKey: "fake"), d1, d1.Items[0].Id, "id.jpg", "image/jpeg", new MemoryStream(TestData.Jpeg()), null));
        Assert.Equal(0, fake.Calls);
        Assert.Equal(AiVerdict.NotChecked, r1.Document.AiVerdict);

        var d2 = TestData.OpenDossier();
        var r2 = await svc.IngestAsync(new IngestRequest(TestData.Tenant(ai: true, aiKey: "fake"), d2, d2.Items[0].Id, "id.jpg", "image/jpeg", new MemoryStream(TestData.Jpeg()), null));
        Assert.Equal(1, fake.Calls);
        Assert.Equal(AiVerdict.Mismatch, r2.Document.AiVerdict);
        Assert.Equal("passeport", r2.Document.AiDetectedType);
        Assert.Equal("fake", r2.Document.AiProviderKey);
        Assert.Equal(100, r2.Classification!.InputTokens);
        Assert.Equal("carte nationale d'identité ou passeport", fake.LastRequest!.ExpectedDocumentType);
        // Un verdict « mismatch » ne bloque pas le dépôt : c'est une alerte.
        Assert.Single(d2.Items[0].Documents);
    }

    [Fact]
    public async Task Ai_check_skipped_when_item_has_no_expected_type()
    {
        var fake = new FakeClassifier("fake", AiVerdict.Match);
        var d = TestData.OpenDossier();
        await Service(fake).IngestAsync(new IngestRequest(TestData.Tenant(ai: true, aiKey: "fake"), d, d.Items[1].Id, "dom.pdf", "application/pdf", new MemoryStream(TestData.Pdf()), null));
        Assert.Equal(0, fake.Calls);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\x\Mon CV.pdf", "Mon CV.pdf")]
    [InlineData("...", "document")]
    [InlineData("a<b>c.pdf", "abc.pdf")]
    public void File_names_are_sanitized(string input, string expected)
    {
        Assert.Equal(expected, DocumentIngestionService.SanitizeFileName(input));
    }

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "x.bin", "application/pdf")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "x.bin", "image/png")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "contrat.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(new byte[] { 0x00, 0x01 }, "x.txt", "text/plain")]
    public void Sniffer_detects_types(byte[] header, string fileName, string expected)
    {
        Assert.Equal(expected, ContentSniffer.Resolve(header, "text/plain", fileName));
    }
}
