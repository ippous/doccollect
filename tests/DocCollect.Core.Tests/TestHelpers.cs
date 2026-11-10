using DocCollect.Core.Ai;
using DocCollect.Core.Security;
using DocCollect.Core.Storage;
using DocCollect.Domain;
using DocCollect.Shared;

namespace DocCollect.Core.Tests;

internal static class TestData
{
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static Tenant Tenant(bool encrypt = true, bool ai = false, string? aiKey = null) => new()
    {
        Id = TenantId, Name = "Étude Martin", Slug = "etude-martin", EncryptAtRest = encrypt,
        AiVerificationEnabled = ai, AiProviderKey = aiKey
    };

    public static ChecklistTemplate Template(TemplateVisibility visibility = TemplateVisibility.Private, Guid? tenantId = null) => new()
    {
        TenantId = visibility == TemplateVisibility.Public ? null : tenantId ?? TenantId,
        Name = "Vente immobilière",
        Visibility = visibility,
        Items =
        [
            new TemplateItem { Label = "Pièce d'identité", Required = true, AcceptedContentTypes = ["image/*", "application/pdf"], MaxSizeMb = 5, ExpectedDocumentType = "carte nationale d'identité ou passeport", Order = 0 },
            new TemplateItem { Label = "Justificatif de domicile", Required = true, AcceptedContentTypes = ["application/pdf"], MaxSizeMb = 5, Order = 1 },
            new TemplateItem { Label = "Diagnostics", Required = false, MaxFiles = 3, Order = 2 }
        ]
    };

    public static Dossier OpenDossier(DeliveryMode mode = DeliveryMode.AllAtOnce, bool validation = false)
    {
        var d = Dossier.FromTemplate(TenantId, "Vente Dupont", Template());
        d.DeliveryMode = mode;
        d.RequireRequesterValidation = validation;
        d.ProviderEmail = "client@example.com";
        d.Open();
        return d;
    }

    public static StoredDocument Doc(string contentType = "application/pdf", long size = 1000) =>
        new() { FileName = "f.pdf", ContentType = contentType, SizeBytes = size, Sha256 = "x" };

    public static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    public static byte[] Pdf(int size = 2048)
    {
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        "%PDF-1.7\n"u8.CopyTo(bytes);
        return bytes;
    }

    public static byte[] Jpeg(int size = 2048)
    {
        var bytes = new byte[size];
        new Random(7).NextBytes(bytes);
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF;
        return bytes;
    }

    public static IMasterKeyProvider Keys(string current = "k1", params string[] ids)
    {
        var options = new EncryptionOptions { CurrentKeyId = current };
        foreach (var id in ids.Append(current).Distinct())
            options.Keys[id] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + id.GetHashCode())).ToArray());
        return new ConfiguredMasterKeyProvider(options);
    }
}

/// <summary>Répertoire temporaire supprimé à la fin du test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dc-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { /* ignoré */ } }
}

internal sealed class FixedStorageResolver(IDocumentStorage storage, Guid? profileId = null) : IStorageResolver
{
    public Guid ProfileId { get; } = profileId ?? Guid.NewGuid();
    public Dictionary<Guid, IDocumentStorage> Others { get; } = [];

    public Task<ResolvedStorage> ResolveForTenantAsync(Tenant tenant, CancellationToken ct = default) =>
        Task.FromResult(new ResolvedStorage(ProfileId, storage));

    public Task<IDocumentStorage> ResolveAsync(Guid storageProfileId, CancellationToken ct = default) =>
        Task.FromResult(storageProfileId == ProfileId ? storage : Others[storageProfileId]);
}

internal sealed class FakeClassifier(string key, AiVerdict verdict) : IDocumentClassifier
{
    public int Calls { get; private set; }
    public ClassificationRequest? LastRequest { get; private set; }
    public string Key => key;
    public string DisplayName => "Faux";
    public string? Region => "UE";
    public bool SupportsContentType(string contentType) => contentType.StartsWith("image/") || contentType == "application/pdf";
    public Task<ClassificationResult> ClassifyAsync(ClassificationRequest request, CancellationToken ct = default)
    {
        Calls++;
        LastRequest = request;
        return Task.FromResult(new ClassificationResult(verdict, "passeport", 0.93, "ok", 100, 20));
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request);
    }
}
